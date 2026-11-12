namespace MagOps.Infrastructure.Services;

/// <summary>
/// Writes a complete file beside its destination, then atomically installs it.
/// If writing or replacement fails, the previous destination remains intact.
/// </summary>
public static class AtomicFileWriter
{
    public static string Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("目标文件没有父目录。");
        Directory.CreateDirectory(directory);

        string stagingPath = fullPath + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
                File.Replace(stagingPath, fullPath, destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            else
                File.Move(stagingPath, fullPath);

            return fullPath;
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }
}
