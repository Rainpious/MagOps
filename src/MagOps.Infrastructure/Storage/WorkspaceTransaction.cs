using System.Security.Cryptography;
using System.Text;

namespace MagOps.Infrastructure.Storage;

/// <summary>
/// Serializes a complete synchronous read/modify/write operation across
/// threads and processes. Keep the updater free of awaits and external calls.
/// </summary>
public static class WorkspaceTransaction
{
    public static T Update<T>(string root, string jsonPath, T fallback,
        Func<T, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(jsonPath);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("JSON must belong to this project root.", nameof(jsonPath));
        string mutexName = "MagOpsTxn_" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fullRoot.ToUpperInvariant())))[..32];
        using var mutex = new Mutex(false, mutexName);
        bool entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new TimeoutException("Timed out waiting for the project transaction.");
            using var fileLock = WorkspaceFileLock.AcquireForRoot(fullRoot);
            var store = new JsonStore();
            T next = update(store.Read(fullPath, fallback));
            store.Write(fullPath, next);
            return next;
        }
        finally
        {
            if (entered) mutex.ReleaseMutex();
        }
    }
}
