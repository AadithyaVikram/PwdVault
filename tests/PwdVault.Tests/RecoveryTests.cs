using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PwdVault.Core;
using PwdVault.Security;
using PwdVault.Storage;
using Xunit;

namespace PwdVault.Tests;

public sealed class RecoveryTests : IDisposable
{
    private const string Passphrase = "synthetic recovery test passphrase";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PwdVault-recovery-" + Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_directory, "synthetic.pwdvault");
    public RecoveryTests()
    {
        Directory.CreateDirectory(_directory);
        using var vault = VaultFile.Create(VaultPath, Passphrase);
        vault.SaveEntry(new VaultEntry { Title = "original", Password = "synthetic original" });
    }

    [Theory]
    [InlineData("TemporaryCreated")]
    [InlineData("ContentWritten")]
    [InlineData("Flushed")]
    public void FailureBeforeCommitPreservesFileAndRollsBackEdit(string stage)
    {
        byte[] original = File.ReadAllBytes(VaultPath);
        using var vault = VaultFile.Open(VaultPath, Passphrase,
            new AtomicFileWriter(current => { if (current.ToString() == stage) throw new IOException("Injected write failure."); }));
        Assert.Throws<IOException>(() => vault.SaveEntry(new VaultEntry { Title = "must not persist" }));
        Assert.Equal("original", Assert.Single(vault.Session.Entries).Title);
        Assert.Equal(original, File.ReadAllBytes(VaultPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void PartialDiskFullWritePreservesVaultAndPassphrase()
    {
        byte[] original = File.ReadAllBytes(VaultPath);
        using var vault = VaultFile.Open(VaultPath, Passphrase, new AtomicFileWriter(createTemporary: path => new DiskFullStream(path)));
        Assert.Throws<IOException>(() => vault.ChangePassphrase(Passphrase, "new synthetic passphrase"));
        Assert.Equal(original, File.ReadAllBytes(VaultPath));
        Assert.Equal("original", Assert.Single(vault.Session.Entries).Title);
        using var stillOpens = VaultCodec.Unlock(vault.Session.Encrypt(), Passphrase);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void FailedRestoreLeavesCurrentSessionAndEncryptedFileIntact()
    {
        string backup = Path.Combine(_directory, "backup.pwdvault");
        using (var other = VaultFile.Create(backup, Passphrase))
            other.SaveEntry(new VaultEntry { Title = "backup entry" });
        byte[] original = File.ReadAllBytes(VaultPath);
        using var vault = VaultFile.Open(VaultPath, Passphrase, new AtomicFileWriter(createTemporary: path => new DiskFullStream(path)));
        Assert.Throws<IOException>(() => vault.Restore(backup, Passphrase));
        Assert.Equal(original, File.ReadAllBytes(VaultPath));
        Assert.Equal("original", Assert.Single(vault.Session.Entries).Title);
    }

    [Theory]
    [InlineData("TemporaryCreated", false)]
    [InlineData("ContentWritten", false)]
    [InlineData("Flushed", false)]
    [InlineData("Committed", true)]
    public async Task KillingSaveProcessLeavesAValidRecoverableVault(string stage, bool committed)
    {
        string configuration = typeof(RecoveryTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        string worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tools/PwdVault.RecoveryWorker/bin", configuration, "net10.0/PwdVault.RecoveryWorker.dll"));
        string host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        Assert.True(File.Exists(worker), "Build the recovery worker project before running this test.");
        string ready = Path.Combine(_directory, "child-ready");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(worker); start.ArgumentList.Add(VaultPath); start.ArgumentList.Add(stage); start.ArgumentList.Add(ready);
        using var process = Process.Start(start)!;
        try
        {
            long began = Stopwatch.GetTimestamp();
            while (!File.Exists(ready) && !process.HasExited && Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(30))
                await Task.Delay(25);
            Assert.True(File.Exists(ready), "Child did not reach the selected save checkpoint.");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            using var reopened = VaultFile.Open(VaultPath, Passphrase); // also proves the lease was released.
            Assert.Contains(reopened.Session.Entries, e => e.Title == "original");
            Assert.Equal(committed ? 2 : 1, reopened.Session.Entries.Count);
            if (committed)
            {
                using var previous = VaultCodec.Unlock(File.ReadAllBytes(VaultPath + ".previous"), Passphrase);
                Assert.Equal("original", Assert.Single(previous.Entries).Title);
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed class DiskFullStream(string path) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            _file.Write(buffer, offset, Math.Min(count, 17));
            throw new IOException("Injected disk-full failure after partial ciphertext write.", unchecked((int)0x80070070));
        }
        public override void Flush() => _file.Flush(true);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    }

    public void Dispose()
    {
        string actual = Path.GetFullPath(_directory);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!actual.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(actual).StartsWith("PwdVault-recovery-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean a directory outside the synthetic test area.");
        Directory.Delete(actual, recursive: true);
    }
}
