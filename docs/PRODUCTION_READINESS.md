# Production-readiness review

Review date: 25 October 2026. Status: **development build; production approval withheld**.

This is a developer review and local validation pass, not an independent security audit. Signing credentials and an independent reviewer are not currently available. Personal vaults were not opened or used as test inputs.

## Fixes in this pass

- Disabled text-box undo history so clearing forms does not intentionally leave an undo stack of prior secrets. The inactivity timeout now clears the locked passphrase form as well as unlocked entries.
- Reject malformed Unicode master passphrases instead of silently replacing invalid UTF-16 characters during UTF-8 encoding. Valid Unicode passphrases retain their existing format and are not normalized.
- Require all entry fields in the authenticated JSON payload, including the identifier. Missing identifiers no longer silently acquire generated values on deserialization.
- Reject clipboard text containing an embedded null or invalid Unicode before altering the clipboard. Preserve cleanup tracking for an earlier password if a new copy fails before emptying the clipboard.
- Ensure clipboard encoding buffers are cleared even when the clipboard is busy, and release the clipboard if work inside the open operation fails.
- Release partial Windows event subscriptions if initialization fails.
- Extracted the atomic file writer for deterministic write-failure and subprocess interruption tests. Cleanup of an encrypted temporary file cannot mask the original write error.
- Replaced in-place application copying with verified versioned application folders, a deployment lease, guarded activation, rollback, and manifest-based uninstall. Updates preserve the prior version rather than overwriting its binaries.

The vault format remains version 1. Files written by the app with valid passphrases and complete entries are compatible. No migration rewrites users' vaults.

## Evidence and limits

| Check | Local result | What this establishes |
| --- | --- | --- |
| Automated C# suite | 45 tests passed | Includes existing tests and new hardening/recovery tests |
| Argon2id known-answer test | Passed | Library result matches RFC 9106 section 5.3; not a review of every cryptographic path |
| Invalid and authenticated malformed inputs | Passed | Bounded random files, oversize inputs, missing fields, null entries, and invalid identifiers are rejected |
| Partial disk-full write simulation | Passed | Injected partial-write failure preserves the prior vault and passphrase/session |
| Process interruption | Passed | A separate child was killed at temporary creation, ciphertext write, flush, and committed replacement; the resulting active file remained decryptable and its lease could be reacquired |
| Update/rollback/uninstall | Passed in isolated folders | Failed activation restores prior shortcuts; changed packages cannot be activated; uninstall retains vault sentinels and unexpected/modified files |
| Dependency vulnerability query | No known vulnerable packages reported | NuGet query of direct and transitive dependencies on this date; not proof that dependencies are free of flaws |
| Native clipboard probe | Blocked: Win32 error 5 | The OS denied creation of an isolated window station. No test fallback touches the interactive clipboard |
| Synthetic unlock benchmark | 167.3, 173.3, and 167.8 ms on this computer | Provisional 64 MiB / 3-pass / 1-lane configuration; not minimum-hardware qualification |
| Installed hardened build | Startup check passed | Versioned package verified and Desktop/Start menu shortcuts updated; personal vaults left untouched |
| Windows signature | NotSigned | Confirmed with Get-AuthenticodeSignature; no claim of publisher authentication |
| Hosted CI | [Passed on the GitHub Windows runner](https://github.com/AadithyaVikram/PwdVault/actions/runs/40188974199) for commit 9138e12 | Locked restore, build, 45 tests, dependency audit, deployment recovery checks, and unsigned publish passed; interactive acceptance remains separate |
| Independent review and signing | Not completed | External release gates remain open |

Local artifacts are under artifacts/hardening and are ignored by Git. The TRX result and dependency-audit JSON are machine-generated evidence. SHA256SUMS.txt describes the published files. Installer tests retain their synthetic evidence under artifacts/deployment-tests.

The process-kill tests do not simulate power loss, controller write caches, all filesystems, or disk removal. The disk-full test injects the corresponding I/O failure after a partial write; it does not physically fill the user's disk.

## Running validation

Run on a Windows development/test machine with the .NET SDK required by global.json:

```powershell
.\scripts\Validate-Release.ps1
```

The script builds the solution, runs tests, tests deployment in isolated project folders, queries NuGet advisories, publishes an unsigned Windows build, and writes checksums. It does not install into the real user profile or publish a GitHub release.

Optional native probe, only on a machine that permits noninteractive window stations:

```powershell
dotnet build tools/PwdVault.WindowsProbe --configuration Release
dotnet tools/PwdVault.WindowsProbe/bin/Release/net10.0/PwdVault.WindowsProbe.dll
```

A window station owns a distinct clipboard. The probe requests a new noninteractive station and desktop; if that fails, it exits with failure. It does not switch the user's visible desktop, inspect clipboard contents, lock Windows, or suspend the machine. Successful flag publication would still not establish that real Windows history/cloud services honor the flags.

A separate, noninteractive benchmark measures synthetic unlocks without requiring native clipboard permissions:

```powershell
dotnet tools/PwdVault.WindowsProbe/bin/Release/net10.0/PwdVault.WindowsProbe.dll --benchmark-only
```

Results from one computer do not set minimum hardware requirements or calibrate every user's machine.

## Interactive acceptance checklist

Use a disposable test vault with invented entries and an independent encrypted backup. Record the Windows build, filesystem, hardware, result, and observed errors for each case.

- Verify the 20-second clipboard timeout, identical subsequent copies, repeated copying, and clipboard contention.
- Enable Windows clipboard history and inspect Win+V after copying a synthetic password. Test cloud clipboard on two test devices. Test third-party clipboard tools separately; exclusion is not guaranteed for those tools.
- Verify lock on Win+L, session disconnect, suspend and resume. Confirm all entries, tooltips, dialogs, and passphrase fields disappear and cannot be restored through undo. Check the five-minute inactivity boundary.
- Interrupt a save and restore on a disposable VM, exercise actual disk exhaustion and removal where appropriate, and verify the active file, previous copy, and external backup.
- Interrupt installation before and during shortcut activation. Each shortcut should lead to a complete version. Verify recovery using the retained version or rerunning installation.
- Exercise keyboard-only navigation, screen-reader announcements without revealing passwords, high-DPI displays, long entries, and error recovery.
- Install, update, rollback, and uninstall on a clean supported Windows x64 machine with no SDK/runtime installed. Verify rollback can read the vault format used by the newer version.
- Measure unlock time and peak working set on minimum supported hardware. Review the current 64 MiB / 3-pass / 1-lane Argon2id settings before freezing requirements.

None of these pending checks is implied to have passed by a successful startup screenshot.

## Deployment behavior

The root remains %LOCALAPPDATA%/Programs/PwdVault. Complete packages live in apps/<content-id>, and Desktop/Start menu shortcuts target the selected version directly. install-state.json records active and previous versions. Existing legacy binaries in the root are retained and are not imported into managed rollback history.

Rollback becomes available after two versioned installations. Run scripts/Rollback-Local.ps1 with the app closed. It verifies the retained package before changing shortcuts; it does not downgrade or modify vault data. Future vault-format changes require an explicit rollback compatibility policy.

Run scripts/Uninstall-Local.ps1 with the app closed. Only unchanged, manifest-listed package files and owned shortcuts are removed. Vaults, modified files, orphan staging folders, unrecognized files, and legacy root binaries are preserved. Installation does not enable startup at Windows login.

Activation is not a single transaction spanning two shortcuts and the state file. A process interruption can temporarily leave shortcuts pointing to different complete versions; it does not intentionally create a mixed-binary application directory. Rerunning installation repairs activation. True power-loss durability remains a separate acceptance test.

Hashes detect accidental corruption relative to a manifest; they do not authenticate a publisher if an attacker can replace both the files and their manifest.

## External release gates

1. Independent review of the vault format, implementation, dependencies, Windows integrations, and release artifacts; fix findings and retain the review record.
2. Publisher identity and a trusted Windows code-signing certificate/provider. Sign the final artifacts, verify the signature and timestamp on a clean machine, and retain verification evidence. Do not treat a self-signed developer certificate as public trust.
3. Complete the interactive and clean-machine checklist, including actual recovery testing.
4. Select supported Windows versions/filesystems/hardware, establish a release/update policy, and approve the evidence before labeling the build production-ready.

References:
- [Argon2id known-answer vector](https://www.rfc-editor.org/rfc/rfc9106#section-5.3)
- [Windows window stations and desktops](https://learn.microsoft.com/en-us/windows/win32/winstation/about-window-stations-and-desktops)
- [Windows clipboard exclusion formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats)
