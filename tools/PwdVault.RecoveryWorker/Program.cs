using PwdVault.Core;
using PwdVault.Storage;

// A deliberately blocked child process used only with synthetic test vaults.
if (args.Length != 3 || !Enum.TryParse<AtomicWriteStage>(args[1], out var stage))
    return 2;
string path = Path.GetFullPath(args[0]);
string directory = Path.GetDirectoryName(path)!;
if (!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
    !Path.GetFileName(directory).StartsWith("PwdVault-recovery-", StringComparison.Ordinal))
    return 3;
var writer = new AtomicFileWriter(current =>
{
    if (current != stage) return;
    File.WriteAllText(args[2], "ready");
    using var blocked = new ManualResetEvent(false);
    blocked.WaitOne();
});
using var vault = VaultFile.Open(path, "synthetic recovery test passphrase", writer);
vault.SaveEntry(new VaultEntry { Title = "new entry", Password = "synthetic child test data" });
return 0;
