using System.Security.Cryptography;
using PwdVault.Core;

namespace PwdVault.Security;

public sealed class VaultSession : IDisposable
{
    internal byte[] Header { get; private set; }
    internal byte[] WrappedKey { get; private set; }
    internal byte[] Key { get; }
    private readonly List<VaultEntry> _entries;
    private bool _disposed;
    public IReadOnlyList<VaultEntry> Entries => _entries.AsReadOnly();

    internal VaultSession(byte[] header, byte[] wrappedKey, byte[] key, List<VaultEntry> entries)
    { Header = header; WrappedKey = wrappedKey; Key = key; _entries = entries; }

    public void Upsert(VaultEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        entry.Validate();
        int index = _entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0) _entries.Add(entry); else _entries[index] = entry;
    }

    public void Delete(Guid id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _entries.RemoveAll(e => e.Id == id);
    }

    public byte[] Encrypt()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return VaultCodec.Encode(this);
    }

    public void ChangePassphrase(string passphrase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        (Header, WrappedKey) = VaultCodec.Rewrap(this, passphrase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(Key);
        _entries.Clear();
    }
}
