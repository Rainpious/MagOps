using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MagOps.Core.Analysis;

namespace MagOps.Infrastructure.Services;

public sealed record OasisGdbMergeExecutionResult(
    string OutputPath,
    long OutputBytes,
    int DestinationChannelCount,
    int SourceChannelCount,
    int MappingCount);

/// <summary>
/// Safe bridge to the official Geosoft GX Python API for reading and appending Oasis GDB files.
/// MagOps deliberately does not edit the proprietary GDB binary structure directly.
/// </summary>
public sealed class OasisGdbService
{
    private const string ProbeMarker = "MAGOPS_GEOSOFT_OK";

    public string ResolveGeosoftPython(string? preferred = null)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, preferred);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("MAGOPS_GEOSOFT_PYTHON"));

        string? pythonHome = Environment.GetEnvironmentVariable("PYTHONHOME");
        if (!string.IsNullOrWhiteSpace(pythonHome))
            AddCandidate(candidates, Path.Combine(pythonHome, "python.exe"));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        for (int minor = 14; minor >= 8; minor--)
            AddCandidate(candidates, Path.Combine(local, "Programs", "Python", $"Python3{minor}", "python.exe"));

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        AddCandidate(candidates, Path.Combine(programData, "Anaconda3", "python.exe"));
        AddCandidate(candidates, Path.Combine(programData, "Miniconda3", "python.exe"));

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddCandidate(candidates, Path.Combine(user, "anaconda3", "python.exe"));
        AddCandidate(candidates, Path.Combine(user, "miniconda3", "python.exe"));

        AddGeosoftInstallCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddGeosoftInstallCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        AddCandidate(candidates, "python.exe");
        AddCandidate(candidates, "python3.exe");

        var checkedCandidates = new List<string>();
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            checkedCandidates.Add(candidate);
            if (TryProbeGeosoftPython(candidate, out _))
                return candidate;
        }

        throw new InvalidOperationException(
            "未检测到可用的 Oasis/Geosoft Python 环境。\r\n" +
            "请在 Oasis montaj 的 Python 设置中确认当前 Python 环境，然后在 MagOps 的 Oasis处理页选择同一个 python.exe。\r\n" +
            "已检查：" + string.Join("；", checkedCandidates));
    }

    public bool TryProbeGeosoftPython(string pythonExecutable, out string message)
    {
        pythonExecutable = NormalizeExecutable(pythonExecutable);
        try
        {
            ProcessRunResult result = RunProcess(
                pythonExecutable,
                ["-c", "import geosoft.gxpy, geosoft.gxpy.gdb, geosoft.gxapi; print('MAGOPS_GEOSOFT_OK')"],
                timeoutMilliseconds: 8_000);
            if (result.ExitCode == 0 && result.StandardOutput.Contains(ProbeMarker, StringComparison.Ordinal))
            {
                message = "Geosoft GX Python 环境可用。";
                return true;
            }

            message = BuildProcessError("Geosoft Python 检测失败", result);
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public OasisGdbInspection Inspect(string pythonExecutable, string gdbPath)
    {
        gdbPath = ValidateGdbPath(gdbPath, mustExist: true);
        string resultPath = NewTempFile("inspect", ".json");
        try
        {
            ProcessRunResult run = RunHelper(
                pythonExecutable,
                ["inspect", gdbPath, resultPath],
                timeoutMilliseconds: 180_000);
            ThrowIfHelperFailed(run, resultPath, "读取 Oasis GDB 表头失败");

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(resultPath, Encoding.UTF8));
            JsonElement root = doc.RootElement;
            return new OasisGdbInspection(
                Path.GetFullPath(gdbPath),
                ReadStringArray(root, "channels"),
                ReadStringArray(root, "lines"),
                ReadStringArray(root, "placeholder_channels"));
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    public OasisGdbMergeExecutionResult Merge(
        string pythonExecutable,
        string destinationBasePath,
        string sourceAppendPath,
        string outputPath,
        IReadOnlyList<OasisChannelMapping> mappings)
    {
        destinationBasePath = ValidateGdbPath(destinationBasePath, mustExist: true);
        sourceAppendPath = ValidateGdbPath(sourceAppendPath, mustExist: true);
        outputPath = ValidateGdbPath(outputPath, mustExist: false);

        if (Path.GetFullPath(outputPath).Equals(Path.GetFullPath(destinationBasePath), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(outputPath).Equals(Path.GetFullPath(sourceAppendPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("输出 GDB 必须使用新文件，不能覆盖两个输入数据库。 ");
        }

        string mappingPath = NewTempFile("mapping", ".json");
        string resultPath = NewTempFile("merge", ".json");
        try
        {
            var payload = mappings.Select(x => new
            {
                destination = x.DestinationChannel,
                source = x.SourceChannel,
                mode = x.MatchMode
            });
            File.WriteAllText(
                mappingPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            ProcessRunResult run = RunHelper(
                pythonExecutable,
                ["merge", destinationBasePath, sourceAppendPath, outputPath, mappingPath, resultPath],
                timeoutMilliseconds: 600_000);
            ThrowIfHelperFailed(run, resultPath, "Oasis GDB 合并失败");

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(resultPath, Encoding.UTF8));
            JsonElement root = doc.RootElement;
            if (!File.Exists(outputPath))
                throw new InvalidDataException("Geosoft API 返回成功，但输出 GDB 不存在。 ");

            return new OasisGdbMergeExecutionResult(
                Path.GetFullPath(outputPath),
                new FileInfo(outputPath).Length,
                root.TryGetProperty("destination_channel_count", out JsonElement dc) ? dc.GetInt32() : 0,
                root.TryGetProperty("source_channel_count", out JsonElement sc) ? sc.GetInt32() : 0,
                root.TryGetProperty("mapping_count", out JsonElement mc) ? mc.GetInt32() : mappings.Count);
        }
        finally
        {
            TryDelete(mappingPath);
            TryDelete(resultPath);
        }
    }

    private static string ValidateGdbPath(string path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("GDB 文件路径不能为空。 ");
        string full = Path.GetFullPath(path.Trim());
        if (!full.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 Geosoft/Oasis .gdb 数据库文件。 ");
        if (mustExist && !File.Exists(full))
            throw new FileNotFoundException("GDB 文件不存在。", full);
        return full;
    }

    private static void AddCandidate(List<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
            candidates.Add(candidate.Trim().Trim('"'));
    }

    private static void AddGeosoftInstallCandidates(List<string> candidates, string? programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles))
            return;

        foreach (string relative in new[]
                 {
                     Path.Combine("Geosoft", "Desktop Applications"),
                     Path.Combine("Seequent", "Desktop Applications")
                 })
        {
            string root = Path.Combine(programFiles, relative);
            AddCandidate(candidates, Path.Combine(root, "python.exe"));
            AddCandidate(candidates, Path.Combine(root, "python", "python.exe"));
            AddCandidate(candidates, Path.Combine(root, "bin", "python.exe"));

            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (string candidate in Directory.EnumerateFiles(root, "python.exe", SearchOption.AllDirectories).Take(12))
                    AddCandidate(candidates, candidate);
            }
            catch
            {
                // Installation probing is best-effort; the UI still allows manual selection.
            }
        }
    }

    private static string NormalizeExecutable(string pythonExecutable)
    {
        if (string.IsNullOrWhiteSpace(pythonExecutable))
            throw new InvalidOperationException("请选择 Oasis/Geosoft Python 的 python.exe。 ");
        return pythonExecutable.Trim().Trim('"');
    }

    private static string[] ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }

    private static void ThrowIfHelperFailed(ProcessRunResult run, string resultPath, string title)
    {
        string helperError = "";
        if (File.Exists(resultPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(resultPath, Encoding.UTF8));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("ok", out JsonElement ok) && !ok.GetBoolean() &&
                    root.TryGetProperty("error", out JsonElement error))
                {
                    helperError = error.GetString() ?? "";
                }
            }
            catch
            {
                // Keep process diagnostics below.
            }
        }

        if (run.ExitCode == 0 && string.IsNullOrWhiteSpace(helperError))
            return;

        string diagnostic = BuildProcessError(title, run);
        if (!string.IsNullOrWhiteSpace(helperError))
            diagnostic = title + "：\r\n" + helperError + "\r\n\r\n" + diagnostic;
        throw new InvalidOperationException(diagnostic);
    }

    private static string BuildProcessError(string title, ProcessRunResult result)
    {
        string output = result.StandardOutput.Trim();
        string error = result.StandardError.Trim();
        var sb = new StringBuilder();
        sb.Append(title).Append("（退出码 ").Append(result.ExitCode).Append("）。");
        if (!string.IsNullOrWhiteSpace(error))
            sb.Append("\r\n").Append(error);
        if (!string.IsNullOrWhiteSpace(output))
            sb.Append("\r\n").Append(output);
        return sb.ToString();
    }

    private static ProcessRunResult RunHelper(
        string pythonExecutable,
        IReadOnlyList<string> arguments,
        int timeoutMilliseconds)
    {
        string script = EnsureHelperScript();
        var fullArguments = new List<string> { script };
        fullArguments.AddRange(arguments);
        return RunProcess(NormalizeExecutable(pythonExecutable), fullArguments, timeoutMilliseconds);
    }

    private static ProcessRunResult RunProcess(
        string executable,
        IReadOnlyList<string> arguments,
        int timeoutMilliseconds)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动 Python：{executable}");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Oasis/Geosoft Python 处理超时。 ");
        }

        Task.WaitAll(new Task[] { stdout, stderr }, 5_000);
        return new ProcessRunResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static string EnsureHelperScript()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MagOps", "OasisGdb");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "magops_oasis_gdb_helper_v1.py");
        if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != HelperScript)
            File.WriteAllText(path, HelperScript, new UTF8Encoding(false));
        return path;
    }

    private static string NewTempFile(string prefix, string extension)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MagOps", "OasisGdb");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}_{Guid.NewGuid():N}{extension}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temp cleanup must never hide the real processing result.
        }
    }

    private sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

    private const string HelperScript = """
import json
import os
import re
import shutil
import sys
import tempfile
import traceback

import numpy as np
import geosoft.gxpy as gxpy
import geosoft.gxpy.gdb as gxdb
import geosoft.gxapi as gxapi

PLACEHOLDER_RE = re.compile(r"^i(?:---|\d{3})$", re.IGNORECASE)


def write_result(path, payload):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)


def channel_names(db):
    channels = db.list_channels()
    if hasattr(channels, "keys"):
        return [str(x) for x in channels.keys()]
    return [str(x) for x in channels]


def line_names(db):
    return [str(x) for x in db.list_lines()]


def value_text(value):
    if isinstance(value, (bytes, bytearray, np.bytes_)):
        raw = bytes(value)
        for encoding in ("utf-8", "gb18030", "latin1"):
            try:
                return raw.decode(encoding).replace("\x00", "").strip()
            except Exception:
                pass
        return repr(raw)
    return str(value).replace("\x00", "").strip()


def is_placeholder_channel(db, channel):
    checked = 0
    for line in db.list_lines():
        try:
            data, fid = db.read_channel(line, channel)
        except Exception:
            continue
        try:
            flat = np.asarray(data).reshape(-1)
        except Exception:
            continue
        if flat.size == 0:
            continue
        # A small sample is sufficient because this special column is a repeated
        # Oasis placeholder such as i--- / i010 / i027.
        step = max(1, int(flat.size // 48))
        for value in flat[::step][:64]:
            text = value_text(value)
            if text in ("", "*", "nan", "None"):
                continue
            checked += 1
            if not PLACEHOLDER_RE.fullmatch(text):
                return False
        if checked >= 24:
            break
    return checked > 0


def inspect_database(path):
    with gxdb.Geosoft_gdb.open(path) as db:
        channels = channel_names(db)
        placeholders = [c for c in channels if is_placeholder_channel(db, c)]
        return {
            "ok": True,
            "path": os.path.abspath(path),
            "channels": channels,
            "lines": line_names(db),
            "placeholder_channels": placeholders,
        }


def copy_alias_channel(db, source_name, destination_name):
    if source_name.casefold() == destination_name.casefold():
        return
    alias = gxdb.Channel.new(db, destination_name, dup=source_name, replace=True)
    for line in db.list_lines():
        try:
            data, fid = db.read_channel(line, source_name)
        except Exception:
            continue
        db.write_channel(line, alias, data, fid)


def merge_databases(base_path, append_path, output_path, mapping_path):
    with open(mapping_path, "r", encoding="utf-8") as f:
        mappings = json.load(f)

    base_abs = os.path.abspath(base_path)
    append_abs = os.path.abspath(append_path)
    output_abs = os.path.abspath(output_path)
    if output_abs.casefold() in (base_abs.casefold(), append_abs.casefold()):
        raise RuntimeError("输出 GDB 不能覆盖输入数据库。")

    os.makedirs(os.path.dirname(output_abs) or os.getcwd(), exist_ok=True)
    if os.path.exists(output_abs):
        os.remove(output_abs)
    shutil.copy2(base_abs, output_abs)

    temp_dir = tempfile.mkdtemp(prefix="magops_oasis_gdb_")
    temp_source = os.path.join(temp_dir, os.path.basename(append_abs))
    shutil.copy2(append_abs, temp_source)
    try:
        with gxdb.Geosoft_gdb.open(temp_source) as source_db:
            source_channels = channel_names(source_db)
            source_by_name = {x.casefold(): x for x in source_channels}
            for item in mappings:
                source_name = str(item.get("source", "")).strip()
                destination_name = str(item.get("destination", "")).strip()
                if not source_name or not destination_name:
                    continue
                canonical_source = source_by_name.get(source_name.casefold())
                if canonical_source is None:
                    raise RuntimeError("追加数据库中不存在表头：" + source_name)
                copy_alias_channel(source_db, canonical_source, destination_name)

        with gxdb.Geosoft_gdb.open(temp_source) as source_db:
            with gxdb.Geosoft_gdb.open(output_abs) as destination_db:
                destination_count = len(channel_names(destination_db))
                source_count = len(channel_names(source_db))
                # GXDU.append is the official database append primitive. It appends
                # data where source/destination channel names match and ignores
                # source-only channels. Alias channels above implement user mapping.
                gxapi.GXDU.append(source_db.gxdb, destination_db.gxdb, 0)

        return {
            "ok": True,
            "output": output_abs,
            "destination_channel_count": destination_count,
            "source_channel_count": source_count,
            "mapping_count": len(mappings),
        }
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def main():
    if len(sys.argv) < 2:
        raise RuntimeError("缺少命令。")
    command = sys.argv[1].lower()
    if command == "inspect":
        if len(sys.argv) != 4:
            raise RuntimeError("inspect 参数错误。")
        gdb_path, result_path = sys.argv[2], sys.argv[3]
        try:
            with gxpy.gx.GXpy():
                result = inspect_database(gdb_path)
            write_result(result_path, result)
        except Exception as ex:
            write_result(result_path, {"ok": False, "error": str(ex), "trace": traceback.format_exc()})
            raise
        return

    if command == "merge":
        if len(sys.argv) != 7:
            raise RuntimeError("merge 参数错误。")
        base_path, append_path, output_path, mapping_path, result_path = sys.argv[2:7]
        try:
            with gxpy.gx.GXpy():
                result = merge_databases(base_path, append_path, output_path, mapping_path)
            write_result(result_path, result)
        except Exception as ex:
            write_result(result_path, {"ok": False, "error": str(ex), "trace": traceback.format_exc()})
            raise
        return

    raise RuntimeError("未知命令：" + command)


if __name__ == "__main__":
    main()
""";
}
