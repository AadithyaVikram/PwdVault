using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PwdVault.Tests")]
[assembly: InternalsVisibleTo("PwdVault.RecoveryWorker")]

namespace PwdVault.Storage;

internal enum AtomicWriteStage { TemporaryCreated, ContentWritten, Flushed, Committed }

// Test observers are instance-scoped and inaccessible through the public application API.
internal sealed class AtomicFileWriter(Action<AtomicWriteStage>? observer = null, Func<string, Stream>? createTemporary = null)
{
    internal void Write(string path, byte[] bytes, bool create)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (Stream stream = createTemporary?.Invoke(temp) ??
                new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                observer?.Invoke(AtomicWriteStage.TemporaryCreated);
                stream.Write(bytes);
                observer?.Invoke(AtomicWriteStage.ContentWritten);
                if (stream is FileStream file) file.Flush(flushToDisk: true);
                else stream.Flush();
                observer?.Invoke(AtomicWriteStage.Flushed);
            }
            if (create) File.Move(temp, path, overwrite: false);
            else File.Replace(temp, path, path + ".previous", ignoreMetadataErrors: false);
            observer?.Invoke(AtomicWriteStage.Committed);
        }
        finally
        {
            // Cleanup must not replace the actual write/commit error. Any orphan contains ciphertext only.
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
