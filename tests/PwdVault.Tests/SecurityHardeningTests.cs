using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using Konscious.Security.Cryptography;
using PwdVault.Core;
using PwdVault.Security;
using Xunit;

namespace PwdVault.Tests;

public sealed class SecurityHardeningTests
{
    [Fact]
    public void Argon2idMatchesRfc9106Section53()
    {
        // Independent published known-answer vector, not an encrypt/decrypt round trip.
        using var argon = new Argon2id(Enumerable.Repeat((byte)1, 32).ToArray())
        {
            Salt = Enumerable.Repeat((byte)2, 16).ToArray(),
            KnownSecret = Enumerable.Repeat((byte)3, 8).ToArray(),
            AssociatedData = Enumerable.Repeat((byte)4, 12).ToArray(),
            MemorySize = 32, Iterations = 3, DegreeOfParallelism = 4
        };
        Assert.Equal(Convert.FromHexString("0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659"), argon.GetBytes(32));
    }

    [Fact]
    public void InvalidSurrogatePassphrasesAreRejectedRatherThanCollapsingToReplacementCharacters()
    {
        Assert.Throws<ArgumentException>(() => VaultCodec.Create("long passphrase " + '\ud800'));
        Assert.Throws<ArgumentException>(() => VaultCodec.Create("long passphrase " + '\ud801'));
    }

    [Fact]
    public void ValidUnicodePassphrasesRoundTripWithoutNormalization()
    {
        const string password = "a valid passphrase \U0001F512 e\u0301";
        using var original = VaultCodec.Create(password);
        byte[] file = original.Encrypt();
        using var reopened = VaultCodec.Unlock(file, password);
        Assert.Throws<VaultUnlockException>(() => VaultCodec.Unlock(file, password.Normalize()));
    }

    [Fact]
    public void RandomTruncatedAndOversizedFilesAreRejectedWithoutUnlocking()
    {
        var random = new Random(104729);
        for (int i = 0; i < 256; i++)
        {
            byte[] bytes = new byte[random.Next(0, 1024)];
            random.NextBytes(bytes);
            Assert.Throws<InvalidDataException>(() => VaultCodec.Unlock(bytes, "synthetic passphrase"));
        }
        Assert.Throws<InvalidDataException>(() => VaultCodec.Unlock(new byte[VaultCodec.MaxFileLength + 1], "synthetic passphrase"));
    }

    [Fact]
    public void TimeoutsUseMonotonicElapsedTimeAndCopyingAgainResetsExpiry()
    {
        var clock = new TestClock();
        var timers = new SecurityTimeouts(clock);
        clock.Advance(TimeSpan.FromSeconds(19));
        timers.RecordCopy();
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(timers.ShouldClearCopy);
        timers.RecordCopy();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(timers.ShouldClearCopy);
        timers.ForgetCopy();
        Assert.False(timers.ShouldClearCopy);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(timers.ShouldLock);
        timers.RecordActivity();
        Assert.False(timers.ShouldLock);
        timers.RetryClear();
        Assert.True(timers.ShouldClearCopy);
    }

    [Fact]
    public void AuthenticatedButMalformedEntryPayloadsAreRejected()
    {
        using var session = VaultCodec.Create("synthetic payload validation passphrase");
        byte[] valid = session.Encrypt();
        using var argon = new Argon2id(Encoding.UTF8.GetBytes("synthetic payload validation passphrase"))
        { Salt = valid[28..44], MemorySize = 65536, Iterations = 3, DegreeOfParallelism = 1 };
        byte[] wrappingKey = argon.GetBytes(32), key = new byte[32];
        try
        {
            using (var aes = new AesGcm(wrappingKey, 16))
                aes.Decrypt(valid.AsSpan(60, 12), valid.AsSpan(72, 32), valid.AsSpan(104, 16), key, valid.AsSpan(0, 60));
            foreach (string json in new[] { "null", "{}", "[null]", "[{\"Title\":\"missing fields\"}]",
                "[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Title\":\"account\",\"Username\":\"\",\"Password\":\"\",\"Url\":\"\",\"Notes\":\"\"}]" })
            {
                byte[] plaintext = Encoding.UTF8.GetBytes(json);
                byte[] file = new byte[VaultCodec.PayloadOffset + plaintext.Length + 16];
                valid.AsSpan(0, VaultCodec.PayloadOffset).CopyTo(file);
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(132, 4), plaintext.Length);
                RandomNumberGenerator.Fill(file.AsSpan(120, 12));
                using (var aes = new AesGcm(key, 16))
                    aes.Encrypt(file.AsSpan(120, 12), plaintext, file.AsSpan(136, plaintext.Length), file.AsSpan(file.Length - 16, 16), file.AsSpan(0, 136));
                var error = Record.Exception(() => VaultCodec.Unlock(file, "synthetic payload validation passphrase"));
                Assert.True(error is JsonException or InvalidDataException, "Authenticated malformed entries must fail schema validation.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); CryptographicOperations.ZeroMemory(key); }
    }
    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
}
