using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.Reporting;

internal static class WordWorkfilePackage
{
    internal static (string ProjectId, string Name) Context(string root, string area, string areaId)
    {
        var workspace = new WorkspaceService(new JsonStore());
        var project = workspace.GetWorkspaceReadOnly(root);
        if (string.IsNullOrWhiteSpace(project.ProjectId) || string.IsNullOrWhiteSpace(areaId) ||
            !workspace.GetWorkareas(root).Any(a => a.WorkareaId == areaId &&
                string.Equals(Path.GetFullPath(a.Path, root), Path.GetFullPath(area, root), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("当前工区身份或路径无法核对。");
        return (project.ProjectId, project.DisplayName);
    }

    internal static XElement Xml(ZipArchive zip, string part)
    {
        using var stream = (zip.GetEntry(part) ?? throw new InvalidDataException("工作稿缺少 " + part)).Open();
        return XElement.Load(stream, LoadOptions.PreserveWhitespace);
    }

    internal static void Store(ZipArchive zip, string part, XElement xml)
    {
        zip.GetEntry(part)?.Delete();
        using var stream = zip.CreateEntry(part, CompressionLevel.Optimal).Open();
        xml.Save(stream, SaveOptions.DisableFormatting);
    }

    internal static FileStream ReadSaved(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static string Fingerprint(Stream stream)
    {
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return hash;
    }

    private static string Temporary(string path)
    {
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .docx 文件。");
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, ".magops-word-" + Guid.NewGuid().ToString("N") + ".tmp");
    }

    internal static void Create(string path, Stream template, Action<ZipArchive> initialize)
    {
        string tmp = Temporary(path);
        try
        {
            using (var dest = File.Create(tmp)) template.CopyTo(dest);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Update))
            {
                var types = Xml(zip, "[Content_Types].xml");
                types.Elements().Single(e => (string?)e.Attribute("PartName") == "/word/document.xml")
                    .SetAttributeValue("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
                Store(zip, "[Content_Types].xml", types);
                initialize(zip);
            }
            File.Move(tmp, path);
        }
        finally { File.Delete(tmp); }
    }

    internal static void Update(string path, string fingerprint, Action<ZipArchive> apply)
    {
        string tmp = Temporary(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            if (Fingerprint(file) != fingerprint) throw new IOException("Word 已修改，请重新检查差异。");
            using (var dest = File.Create(tmp)) file.CopyTo(dest);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Update)) apply(zip);
            File.Replace(tmp, path, path + ".bak");
        }
        finally { File.Delete(tmp); }
    }

    internal static void Export(string path, string destination, Action<ZipArchive> validate)
    {
        string tmp = Temporary(destination);
        try
        {
            using var file = ReadSaved(path);
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true)) validate(zip);
            file.Position = 0;
            using (var dest = File.Create(tmp)) file.CopyTo(dest);
            File.Move(tmp, destination);
        }
        finally { File.Delete(tmp); }
    }
}
