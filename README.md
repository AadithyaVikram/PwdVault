# PwdVault

An offline Windows password vault built with C#/.NET 10 and Avalonia 12. The vault engine and file format are separate from the desktop UI and Windows integrations so future desktop ports can reuse them.

This is a development MVP, not an audited password manager. See [production-readiness evidence and remaining release gates](docs/PRODUCTION_READINESS.md).

## Open normally on Windows

After local installation, double-click **PwdVault** on the Desktop or search for **PwdVault** in the Start menu. These shortcuts launch the installed executable directly, with no terminal window. The self-contained build includes its .NET runtime.

The per-user installation root is `%LOCALAPPDATA%\Programs\PwdVault`; verified app versions live under `apps/<content-id>`, and shortcuts select the active version. Your vault files stay in the locations you chose; installation does not move them. This does not enable automatic startup when Windows starts.

To install or update from source, publish the Windows build using the command below, close any installed PwdVault window, then run:

```powershell
.\scripts\Install-Local.ps1
```

The installer stages and verifies a complete package before switching Desktop and Start menu shortcuts. Retained versioned builds support `scripts/Rollback-Local.ps1`; `scripts/Uninstall-Local.ps1` removes only unchanged managed app files and preserves vaults and unknown files. Legacy files from the earlier installer are retained. Code signing and automatic updates remain future work.

## Run from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then open a new terminal:

```powershell
dotnet restore PwdVault.slnx --configfile NuGet.Config
dotnet run --project src/PwdVault.Desktop
```

On this development machine the SDK is also available at C:\Program Files\dotnet\dotnet.exe. Restart an existing IDE terminal if the SDK is not yet on PATH.

Windows 11 x64 is the initial development target. Other operating systems and architectures have not been validated. The core projects target portable net10.0; the first UI release requires Windows security integration before unlocking a vault.

## Use

1. Choose **Choose new location**, select a new .pwdvault filename, and enter and confirm a unique master passphrase of at least 12 characters. Several random words are preferable.
2. Create and save entries. The entry list shows the name, website, and associated username or email. Hover over a shortened value to see it in full. Search matches entry names, websites, and usernames or emails.
3. Passwords are masked by default. Copying sets a 20-second expiry; a subsequent clipboard change is preserved, including a copy of identical text.
4. Save an encrypted backup to a new filename and retain it separately from the active device.
5. Lock manually or close the window to exit. The vault also locks after five minutes without input in the app, on Windows session lock/disconnect, and on suspend/resume.

Edits are saved only with **Save entry**. Switching entries or locking discards unsaved form changes. URLs are stored as text and never opened automatically. A master passphrase cannot be recovered or reset if forgotten.

Restore validates the selected backup's passphrase before replacing the active vault and keeps a uniquely named pre-restore encrypted copy beside it. After restore, the restored vault uses the backup's passphrase. New vaults and backups intentionally refuse to overwrite existing filenames.

## Build and test

```powershell
dotnet build PwdVault.slnx --configuration Release
dotnet test PwdVault.slnx --configuration Release
```

Dependency versions and NuGet lock files are included in the project. Use --locked-mode on restore in CI. Runtime-specific publishes use separate packages.win-x64.lock.json files so packaging does not change the normal build locks. No network access is required by the application itself; installing the SDK and restoring development packages requires downloads.

A framework-dependent build is in src/PwdVault.Desktop/bin/Release/net10.0. For a distributable Windows folder, publish a self-contained build:

```powershell
dotnet publish src/PwdVault.Desktop --configuration Release --runtime win-x64 --self-contained true --output artifacts/win-x64
```

After publishing, run `scripts/Install-Local.ps1` to install this build for your Windows account. Signing and automatic updates are not implemented.

## Project layout

| Project | Responsibility |
| --- | --- |
| PwdVault.Core | Entry model and platform contract |
| PwdVault.Security | Argon2id, AES-GCM, unlocked session, password generation |
| PwdVault.Storage | Versioned files, exclusive lease, safe saves, backup and restore |
| PwdVault.Platform.Windows | Clipboard ownership/exclusion flags and Windows session/power events |
| PwdVault.Desktop | Avalonia UI and application view model |
| PwdVault.Tests | Cryptography, storage failures, clipboard policy, and view-model tests |

The original vector logo is in `src/PwdVault.Desktop/Assets/logo.svg`. Run `scripts/Generate-Icon.ps1` on Windows to regenerate the PNG and multi-resolution ICO exports. The icon is embedded in the executable and displayed in the window and application header.

Run `scripts/Validate-Release.ps1` for build/test/audit/deployment validation and an unsigned Windows publish. The GitHub workflow runs the same checks on dev/main pushes and pull requests. The [first Windows hardening validation passed](https://github.com/AadithyaVikram/PwdVault/actions/runs/40188974199). Detailed results, limits, and manual acceptance steps are in [the readiness report](docs/PRODUCTION_READINESS.md).

See [the project plan](PROJECT_PLAN.md) and [vault format specification](docs/VAULT_FORMAT.md).

## Security and verification limits

- Encryption protects a copied vault at rest. Malware, keyloggers, administrator access, or a compromised application can still capture unlocked secrets.
- .NET strings and framework controls can retain immutable plaintext copies. The app clears its fields and owned mutable buffers, but cannot guarantee complete memory erasure or protection from swap, hibernation, or crash dumps.
- Clipboard exclusion flags request that Windows omit password copies from history and cloud sync. Third-party clipboard tools may ignore them. A busy clipboard is retried while the app stays open; exit cannot guarantee cleanup if another process keeps the clipboard locked.
- Automatic previous versions and backups may retain deleted entries and older passphrases. Their confidentiality depends on the passphrase protecting each copy. There is no promise of secure SSD deletion or rollback detection.
- Local filesystem permissions are inherited from the selected directory. Store vaults in a directory restricted to your Windows account.
- File corruption, a wrong passphrase, or unsupported formats never trigger a reset or automatic overwrite.

Automated tests cover ciphertext modification, malformed headers, invalid KDF resource requests, wrong passwords, passphrase changes, backup restore, competing writers, external edits, failed replacement, lock-during-unlock, cleared view-model fields, password generation, and clipboard ownership policy using a simulated native boundary.

Before release, manually verify Windows clipboard history/sync exclusion, timer behavior and contention, session lock and suspend/resume, disk-full/process-interruption recovery, accessibility, and installation on a clean machine. Full power-loss guarantees, hardware-wide KDF calibration, independent security review, signing, and macOS/Linux adapters remain outstanding.

The optional scripts/Smoke-Test.ps1 script briefly starts a separate app instance, checks initial controls using Windows UI Automation, captures only that app's window into artifacts, and closes that instance. It does not create a vault or touch clipboard contents.

## Verification on this machine

On 24 October 2026: Release build succeeded with zero warnings; 27 automated tests passed; Windows UI Automation startup checks passed; the initial window capture was visually inspected. Native clipboard history/sync exclusion, real session lock/suspend, and the remaining release checks above are still pending.

The self-contained Windows build has also been installed locally with Desktop and Start menu shortcuts. Its logo appears in the application header, title bar, and executable icon. The updated window passed the startup UI check, and the installed build was verified in a normal visible launch.

On 25 October 2026, local hardening expanded the C# suite to 45 tests, added subprocess save-interruption and disk-full simulations, and tested versioned installation, rollback, and safe uninstall. Native clipboard validation was blocked by Windows error 5; interactive clipboard/session/power checks and independent review remain open. See the readiness report for precise evidence and limitations.
