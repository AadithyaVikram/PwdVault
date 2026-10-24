using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PwdVault.Core;
using PwdVault.Desktop;
using PwdVault.Security;
using PwdVault.Storage;
using Xunit;

namespace PwdVault.Tests;

public sealed class VaultTests : IDisposable
{
    private const string Passphrase = "four random words for testing";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PwdVault-tests-" + Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_directory, "test.pwdvault");
    public VaultTests() => Directory.CreateDirectory(_directory);
    private static VaultEntry Example() => new() { Title = "Private account name", Username = "private-user@example.com",
        Password = "not-for-real-use-837461", Url = "https://private.example.com", Notes = "Private note" };

    [Fact]
    public void RoundTripEncryptsAllFieldsAndUsesFreshNonces()
    {
        using var vault = VaultCodec.Create(Passphrase);
        var entry = Example();
        vault.Upsert(entry);
        byte[] first = vault.Encrypt(), second = vault.Encrypt();
        Assert.False(first.AsSpan(120, 12).SequenceEqual(second.AsSpan(120, 12)));
        foreach (string field in new[] { entry.Title, entry.Username, entry.Password, entry.Url, entry.Notes, Passphrase })
            Assert.False(first.AsSpan().IndexOf(Encoding.UTF8.GetBytes(field)) >= 0);
        using var opened = VaultCodec.Unlock(first, Passphrase);
        Assert.Equal(entry, Assert.Single(opened.Entries));
    }

    [Fact]
    public void WrongPassphraseDoesNotUnlock()
    {
        using var vault = VaultCodec.Create(Passphrase);
        Assert.Throws<VaultUnlockException>(() => VaultCodec.Unlock(vault.Encrypt(), "wrong passphrase"));
    }

    [Theory]
    [InlineData(28)]  // KDF salt
    [InlineData(44)]  // vault identifier
    [InlineData(60)]  // wrapped-key nonce
    [InlineData(72)]  // wrapped key
    [InlineData(104)] // wrapped-key tag
    [InlineData(120)] // payload nonce
    [InlineData(136)] // payload ciphertext
    [InlineData(-1)]  // payload tag
    public void TamperingIsRejected(int offset)
    {
        using var vault = VaultCodec.Create(Passphrase);
        byte[] file = vault.Encrypt();
        file[offset < 0 ? file.Length - 1 : offset] ^= 1;
        Assert.Throws<VaultUnlockException>(() => VaultCodec.Unlock(file, Passphrase));
    }

    [Fact]
    public void InvalidHeaderLengthsAndResourceRequestsAreRejectedBeforeDerivingKeys()
    {
        using var vault = VaultCodec.Create(Passphrase);
        byte[] original = vault.Encrypt();
        foreach (int offset in new[] { 8, 12, 16, 20, 24, 132 })
        {
            byte[] file = (byte[])original.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(offset, 4), int.MaxValue);
            Assert.Throws<InvalidDataException>(() => VaultCodec.Unlock(file, Passphrase));
        }
        foreach (int length in new[] { 0, 7, 60, 120, 136, original.Length - 1 })
            Assert.Throws<InvalidDataException>(() => VaultCodec.Unlock(original[..length], Passphrase));
    }

    [Fact]
    public void PassphraseChangePreservesEntriesAndOldBackupsUseOldPassphrase()
    {
        byte[] oldBackup;
        var entry = Example();
        using (var file = VaultFile.Create(VaultPath, Passphrase))
        {
            file.SaveEntry(entry);
            oldBackup = File.ReadAllBytes(VaultPath);
            Assert.Throws<VaultUnlockException>(() => file.ChangePassphrase("incorrect current password", "new long unique passphrase"));
            file.ChangePassphrase(Passphrase, "new long unique passphrase");
        }
        byte[] current = File.ReadAllBytes(VaultPath);
        Assert.Throws<VaultUnlockException>(() => VaultCodec.Unlock(current, Passphrase));
        using var newSession = VaultCodec.Unlock(current, "new long unique passphrase");
        using var oldSession = VaultCodec.Unlock(oldBackup, Passphrase);
        Assert.Equal(entry, Assert.Single(newSession.Entries));
        Assert.Equal(entry, Assert.Single(oldSession.Entries));
    }

    [Fact]
    public void CreateDoesNotOverwriteAndLeaseBlocksSecondWriter()
    {
        using (var first = VaultFile.Create(VaultPath, Passphrase))
            Assert.ThrowsAny<IOException>(() => VaultFile.Open(VaultPath, Passphrase));
        byte[] before = File.ReadAllBytes(VaultPath);
        Assert.ThrowsAny<IOException>(() => VaultFile.Create(VaultPath, Passphrase));
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        using var reopened = VaultFile.Open(VaultPath, Passphrase);
    }

    [Fact]
    public void SavesRetainPreviousValidFileAndDeletePersists()
    {
        var entry = Example();
        using (var file = VaultFile.Create(VaultPath, Passphrase))
        {
            file.SaveEntry(entry);
            byte[] before = File.ReadAllBytes(VaultPath);
            file.DeleteEntry(entry.Id);
            Assert.Equal(before, File.ReadAllBytes(VaultPath + ".previous"));
        }
        using var current = VaultFile.Open(VaultPath, Passphrase);
        Assert.Empty(current.Session.Entries);
        using var previous = VaultCodec.Unlock(File.ReadAllBytes(VaultPath + ".previous"), Passphrase);
        Assert.Equal(entry, Assert.Single(previous.Entries));
    }

    [Fact]
    public void ExternalChangesCannotBeSilentlyOverwrittenAndFailedEditsRollback()
    {
        using var file = VaultFile.Create(VaultPath, Passphrase);
        byte[] changed = File.ReadAllBytes(VaultPath);
        changed[^1] ^= 1;
        File.WriteAllBytes(VaultPath, changed);
        Assert.Throws<IOException>(() => file.SaveEntry(Example()));
        Assert.Empty(file.Session.Entries);
        Assert.Equal(changed, File.ReadAllBytes(VaultPath));
    }

    [Fact]
    public void FailedReplacementPreservesDestinationAndRollsBackMemory()
    {
        using var file = VaultFile.Create(VaultPath, Passphrase);
        byte[] before = File.ReadAllBytes(VaultPath);
        using (var held = new FileStream(VaultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => file.SaveEntry(Example()));
            Assert.Empty(file.Session.Entries);
            Assert.Equal(before, File.ReadAllBytes(VaultPath));
        }
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        file.SaveEntry(Example()); // lease and file remain usable after a failed save.
        Assert.Single(file.Session.Entries);
    }

    [Fact]
    public void RestoreValidatesBackupAndKeepsPreRestoreCopy()
    {
        string backup = Path.Combine(_directory, "backup.pwdvault");
        using var file = VaultFile.Create(VaultPath, Passphrase);
        var entry = Example();
        file.SaveEntry(entry);
        file.ExportBackup(backup);
        Assert.ThrowsAny<IOException>(() => file.ExportBackup(backup));
        file.DeleteEntry(entry.Id);
        byte[] before = File.ReadAllBytes(VaultPath);
        Assert.Throws<VaultUnlockException>(() => file.Restore(backup, "incorrect passphrase"));
        Assert.Equal(before, File.ReadAllBytes(VaultPath));
        Assert.Empty(file.Session.Entries);
        file.Restore(backup, Passphrase);
        Assert.Equal(entry, Assert.Single(file.Session.Entries));
        string retained = Assert.Single(Directory.GetFiles(_directory, "*.before-restore-*.pwdvault"));
        Assert.Equal(before, File.ReadAllBytes(retained));
    }

    [Fact]
    public void DisposedSessionsCannotEncryptOrMutate()
    {
        var session = VaultCodec.Create(Passphrase);
        session.Upsert(Example());
        session.Dispose();
        Assert.Empty(session.Entries);
        Assert.Throws<ObjectDisposedException>(() => session.Encrypt());
        Assert.Throws<ObjectDisposedException>(() => session.Upsert(Example()));
    }

    [Fact]
    public async Task LockDuringUnlockCannotReopenTheInterfaceOrKeepTheLease()
    {
        using (var created = VaultFile.Create(VaultPath, Passphrase)) created.SaveEntry(Example());
        using var vm = new MainViewModel { VaultPath = VaultPath };
        Task opening = vm.OpenAsync(Passphrase, create: false);
        Assert.True(vm.IsBusy);
        vm.Lock();
        await opening;
        Assert.True(vm.IsLocked);
        Assert.Empty(vm.Entries);
        Assert.Equal("", vm.Password);
        using var reopened = VaultFile.Open(VaultPath, Passphrase);
    }

    [Fact]
    public async Task ViewModelClearsFieldsAndHandlesInvalidVaultsWithoutThrowing()
    {
        using var vm = new MainViewModel { VaultPath = VaultPath };
        File.WriteAllText(VaultPath, "not a vault");
        await vm.OpenAsync(Passphrase, create: false);
        Assert.True(vm.IsLocked);
        Assert.Contains("vault", vm.Status, StringComparison.OrdinalIgnoreCase);
        File.Delete(VaultPath);
        await vm.OpenAsync(Passphrase, create: true);
        Assert.True(vm.IsUnlocked);
        vm.EntryTitle = "Account"; vm.Password = "secret value";
        await vm.SaveAsync();
        Assert.Single(vm.Entries);
        vm.Lock();
        Assert.Empty(vm.Entries);
        Assert.Equal("", vm.Password);
        Assert.Equal("", vm.EntryTitle);
        Assert.False(vm.Reveal);
    }

    [Fact]
    public async Task EntryListIncludesWebsiteAndFindsWebsiteOrLoginWithoutExposingPasswords()
    {
        using (var created = VaultFile.Create(VaultPath, Passphrase)) created.SaveEntry(Example());
        using var vm = new MainViewModel { VaultPath = VaultPath };
        await vm.OpenAsync(Passphrase, create: false);
        var summary = Assert.Single(vm.Entries);
        Assert.Equal(Example().Title, summary.Title);
        Assert.Equal(Example().Username, summary.Username);
        Assert.Equal(Example().Url, summary.Url);
        Assert.DoesNotContain(Example().Password, summary.ToString());
        foreach (string search in new[] { "PRIVATE.EXAMPLE.COM", "PRIVATE-USER@", "account name" })
        {
            vm.Search = search;
            Assert.Equal(summary, Assert.Single(vm.Entries));
        }
        vm.Search = "does-not-match";
        Assert.Empty(vm.Entries);
        vm.Search = "";
        vm.Select(Assert.Single(vm.Entries));
        Assert.Equal(Example().Url, vm.Url);
        vm.Url = "https://updated.example.org";
        await vm.SaveAsync();
        Assert.Equal(vm.Url, Assert.Single(vm.Entries).Url);
        vm.Lock();
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void GeneratorEnforcesSelectedSetsAndRejectsInvalidOptions()
    {
        for (int i = 0; i < 30; i++)
        {
            string password = PasswordGenerator.Generate(24);
            Assert.Equal(24, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));
            Assert.All(PasswordGenerator.Generate(18, false, false, true, false), c => Assert.True(char.IsDigit(c)));
        }
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(24, false, false, false, false));
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(7));
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(257));
    }

    public void Dispose()
    {
        // Only the unique directory created by this test is removed.
        Directory.Delete(_directory, recursive: true);
    }
}
