using System.Security.Cryptography;
using PwdVault.Core;
using PwdVault.Security;

namespace PwdVault.Storage;

// A sidecar lease survives atomic file replacement and is released on process exit.
public sealed class VaultFile : IDisposable
{
    public string FilePath { get; }
    public VaultSession Session { get; private set; }
    private readonly FileStream _lease;
    private byte[] _lastHash;
    private bool _disposed;

    private VaultFile(string path, FileStream lease, VaultSession session, byte[] lastBytes)
    { FilePath = path; _lease = lease; Session = session; _lastHash = SHA256.HashData(lastBytes); }

    public static VaultFile Create(string path, string passphrase)
    {
        path = Path.GetFullPath(path);
        var lease = Acquire(path);
        VaultSession? session = null;
        try
        {
            if (File.Exists(path)) throw new IOException("A vault already exists at this location. Choose another filename.");
            session = VaultCodec.Create(passphrase);
            byte[] bytes = session.Encrypt();
            AtomicWrite(path, bytes, create: true);
            return new VaultFile(path, lease, session, bytes);
        }
        catch { session?.Dispose(); lease.Dispose(); throw; }
    }

    public static VaultFile Open(string path, string passphrase)
    {
        path = Path.GetFullPath(path);
        var lease = Acquire(path);
        try
        {
            byte[] bytes = ReadBounded(path);
            return new VaultFile(path, lease, VaultCodec.Unlock(bytes, passphrase), bytes);
        }
        catch { lease.Dispose(); throw; }
    }

    public void SaveEntry(VaultEntry entry)
    {
        var old = Session.Entries.FirstOrDefault(e => e.Id == entry.Id);
        Session.Upsert(entry);
        try { Save(); }
        catch { if (old is null) Session.Delete(entry.Id); else Session.Upsert(old); throw; }
    }

    public void DeleteEntry(Guid id)
    {
        var old = Session.Entries.FirstOrDefault(e => e.Id == id);
        Session.Delete(id);
        try { Save(); }
        catch { if (old is not null) Session.Upsert(old); throw; }
    }

    public void ChangePassphrase(string current, string replacement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] previous = Session.Encrypt();
        using var verified = VaultCodec.Unlock(previous, current);
        Session.ChangePassphrase(replacement);
        try { Save(); }
        catch
        {
            Session.Dispose();
            Session = VaultCodec.Unlock(previous, current);
            throw;
        }
    }

    public void ExportBackup(string destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckUnchanged();
        // Never overwrite an arbitrary existing file, backup, or another live vault.
        AtomicWrite(Path.GetFullPath(destination), Session.Encrypt(), create: true);
    }

    public void Restore(string source, string backupPassphrase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.Equals(Path.GetFullPath(source), FilePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a separate backup file.");
        byte[] backup = ReadBounded(source);
        var restored = VaultCodec.Unlock(backup, backupPassphrase);
        try
        {
            CheckUnchanged();
            // Keep a uniquely named copy before replacing the active vault.
            AtomicWrite(FilePath + ".before-restore-" + Guid.NewGuid().ToString("N") + ".pwdvault", ReadBounded(FilePath), create: true);
            AtomicWrite(FilePath, backup, create: false);
            _lastHash = SHA256.HashData(backup);
            Session.Dispose();
            Session = restored;
        }
        catch { restored.Dispose(); throw; }
    }

    private void Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckUnchanged();
        byte[] bytes = Session.Encrypt();
        AtomicWrite(FilePath, bytes, create: false);
        _lastHash = SHA256.HashData(bytes);
    }

    private void CheckUnchanged()
    {
        if (!CryptographicOperations.FixedTimeEquals(_lastHash, SHA256.HashData(ReadBounded(FilePath))))
            throw new IOException("The vault changed outside this app. Lock and reopen it before saving.");
    }

    private static FileStream Acquire(string path) => new(path + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > VaultCodec.MaxFileLength) throw new InvalidDataException("Vault file exceeds the supported size.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void AtomicWrite(string path, byte[] bytes, bool create)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (create) File.Move(temp, path, overwrite: false);
            else File.Replace(temp, path, path + ".previous", ignoreMetadataErrors: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Session.Dispose();
        _lease.Dispose();
    }
}
