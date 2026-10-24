using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using PwdVault.Core;

namespace PwdVault.Security;

public sealed class VaultUnlockException() : Exception("Incorrect master passphrase or damaged vault.");

// Version 1: fixed, little-endian header; see docs/VAULT_FORMAT.md.
public static class VaultCodec
{
    public const int HeaderLength = 60;
    public const int PayloadOffset = 136;
    public const int MaxPayloadLength = 8 * 1024 * 1024;
    public const int MaxFileLength = PayloadOffset + MaxPayloadLength + 16;
    private static readonly byte[] Magic = "PWDVLT01"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };

    public static VaultSession Create(string passphrase)
    {
        ValidateNewPassphrase(passphrase);
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        Write(header, 8, 1); Write(header, 12, 1);
        Write(header, 16, 65536); Write(header, 20, 3); Write(header, 24, 1);
        RandomNumberGenerator.Fill(header.AsSpan(28, 32));
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try { return new VaultSession(header, Wrap(key, passphrase, header), key, []); }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    public static VaultSession Unlock(byte[] file, string passphrase)
    {
        ValidateFile(file);
        byte[] header = file[..HeaderLength];
        byte[] wrappingKey = Derive(passphrase, header);
        byte[] key = new byte[32];
        byte[] plaintext = new byte[file.Length - PayloadOffset - 16];
        try
        {
            using (var aes = new AesGcm(wrappingKey, 16))
                aes.Decrypt(file.AsSpan(60, 12), file.AsSpan(72, 32), file.AsSpan(104, 16), key, header);
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(file.AsSpan(120, 12), file.AsSpan(PayloadOffset, plaintext.Length),
                    file.AsSpan(file.Length - 16, 16), plaintext, file.AsSpan(0, PayloadOffset));
            var entries = JsonSerializer.Deserialize<List<VaultEntry>>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The vault has no valid entry collection.");
            ValidateEntries(entries);
            return new VaultSession(header, file[60..120], key, entries);
        }
        catch (AuthenticationTagMismatchException) { CryptographicOperations.ZeroMemory(key); throw new VaultUnlockException(); }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static byte[] Encode(VaultSession session)
    {
        ValidateEntries(session.Entries);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(session.Entries, JsonOptions);
        try
        {
            if (plaintext.Length > MaxPayloadLength) throw new InvalidDataException("Vault exceeds the 8 MiB limit.");
            byte[] file = new byte[PayloadOffset + plaintext.Length + 16];
            session.Header.CopyTo(file, 0);
            session.WrappedKey.CopyTo(file, 60);
            RandomNumberGenerator.Fill(file.AsSpan(120, 12));
            Write(file, 132, plaintext.Length);
            using var aes = new AesGcm(session.Key, 16);
            aes.Encrypt(file.AsSpan(120, 12), plaintext, file.AsSpan(PayloadOffset, plaintext.Length),
                file.AsSpan(file.Length - 16, 16), file.AsSpan(0, PayloadOffset));
            return file;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal static (byte[] Header, byte[] Wrapped) Rewrap(VaultSession session, string passphrase)
    {
        ValidateNewPassphrase(passphrase);
        byte[] header = (byte[])session.Header.Clone();
        RandomNumberGenerator.Fill(header.AsSpan(28, 16));
        return (header, Wrap(session.Key, passphrase, header));
    }

    private static byte[] Wrap(byte[] key, string passphrase, byte[] header)
    {
        byte[] wrappingKey = Derive(passphrase, header);
        try
        {
            byte[] wrapped = new byte[60];
            RandomNumberGenerator.Fill(wrapped.AsSpan(0, 12));
            using var aes = new AesGcm(wrappingKey, 16);
            aes.Encrypt(wrapped.AsSpan(0, 12), key, wrapped.AsSpan(12, 32), wrapped.AsSpan(44, 16), header);
            return wrapped;
        }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    private static byte[] Derive(string passphrase, byte[] header)
    {
        if (passphrase.Length > 1024) throw new ArgumentException("Passphrase exceeds 1024 characters.");
        byte[] password = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            using var argon = new Argon2id(password)
            {
                Salt = header[28..44], MemorySize = Read(header, 16),
                Iterations = Read(header, 20), DegreeOfParallelism = Read(header, 24)
            };
            return argon.GetBytes(32);
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    public static void ValidateNewPassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12 || passphrase.Length > 1024)
            throw new ArgumentException("Use a unique master passphrase of 12 to 1024 characters; several random words are preferable.");
    }

    private static void ValidateFile(byte[] file)
    {
        if (file.Length < PayloadOffset + 18 || file.Length > MaxFileLength ||
            !file.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Invalid or truncated vault file.");
        if (Read(file, 8) != 1 || Read(file, 12) != 1) throw new InvalidDataException("Unsupported vault format or algorithm.");
        int memory = Read(file, 16), iterations = Read(file, 20), lanes = Read(file, 24);
        if (memory < 19456 || memory > 262144 || iterations < 2 || iterations > 10 || lanes < 1 || lanes > 4 ||
            (long)memory * iterations > 786432)
            throw new InvalidDataException("Unsupported key-derivation resource parameters.");
        int length = Read(file, 132);
        if (length < 2 || length > MaxPayloadLength || length != file.Length - PayloadOffset - 16)
            throw new InvalidDataException("Invalid encrypted payload length.");
    }

    private static void ValidateEntries(IReadOnlyList<VaultEntry> entries)
    {
        if (entries.Count > 10000) throw new InvalidDataException("Vault exceeds 10,000 entries.");
        var ids = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (entry is null) throw new InvalidDataException("Invalid null entry.");
            entry.Validate();
            if (!ids.Add(entry.Id)) throw new InvalidDataException("Duplicate entry identifier.");
        }
    }

    private static int Read(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Write(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
