# Security review — 1 November 2026

Reviewed upstream commit `1a92475440b2ac79d7eae7a6a999c0d59a9a9507` plus the local fixes described below. A fresh fetch confirmed that `origin/main` was current and `origin/dev` had no additional commits outside main. These fixes are local source changes, not an installed or published release.

PwdVault is a single-user offline Windows password vault. This review traced encryption and passphrase changes, file parsing and saves, backup/restore, view-model locking, Windows clipboard and session integration, dependencies, and deployment scripts. The threat model includes stolen encrypted files, hostile file inputs, accidental secret retention, and interrupted writes. It does not promise protection from malware or an administrator controlling the unlocked machine.

## Fixed: old backup keys could decrypt future entries

Previously, changing the master passphrase rewrapped the same data-encryption key. An attacker with an old backup and its passphrase could recover that key and decrypt a newer vault obtained later, including entries added after the change. Rejecting the old passphrase in the normal unlock API did not prevent this direct attack.

The new regression test recovered a key from a synthetic old backup and successfully decrypted a new entry before the fix. After the fix, that operation fails AES-GCM authentication. Passphrase changes now generate an independent random data key, wrap it with the new passphrase, clear the old session key, and encrypt subsequent saves with the new key. Failed key derivation leaves the original session intact; the existing failed-save test verifies rollback to the prior passphrase and file.

The file format remains version 1 and preserves the vault identifier. Users of older builds must change their passphrase using the fixed build to rotate the key. Existing backups still expose their original contents if their passphrases are compromised. Restoring such a backup reinstates its old key; change the passphrase again afterward. Credentials already disclosed in an old backup must be changed at the relevant services.

This follows the principle of rotating compromised encryption keys and re-encrypting data described in [OWASP's cryptographic storage guidance](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html#key-lifetimes-and-rotation).

## Fixed: revealed-password text-box copy bypass

With password visibility enabled, the text box could use its normal copy/cut path instead of the application's protected clipboard operation. That bypassed the Windows history/cloud exclusion flags and the 20-second ownership-based cleanup.

The password text box now cancels normal copy/cut events and directs users to the protected Copy button. The XAML event wiring compiles and the desktop starts successfully. Keyboard/context-menu copy and cut while revealed still require interactive acceptance testing; the startup test alone does not establish their runtime behavior.

## Validation

- Locked NuGet restore succeeded.
- Release solution build succeeded with zero warnings and errors, including the final clipboard change. The build used `-p:UsedAvaloniaProducts=` to skip Avalonia's build-time telemetry task, which otherwise attempted a sandbox-denied write outside the workspace.
- 51 automated tests passed after key rotation, including the new old-backup attack and failed-rotation tests, existing tampering and malformed-input tests, clipboard-policy tests, and interrupted-save recovery tests. Results: `artifacts/security-review/security-review.trx`.
- NuGet's direct and transitive vulnerability query reported no known vulnerable packages. This query does not prove the absence of undisclosed dependency flaws or audit the bundled .NET runtime.
- Isolated deployment tests passed: installation, update, failed activation recovery, rollback, changed-package rejection, and preservation of synthetic vault files during uninstall.
- Desktop startup smoke test passed after the UI change. Only the test instance was opened; no personal vault was used.

## Remaining limits and release gates

- Independent security review and publisher signing remain outstanding. Package hashes detect corruption relative to a manifest; they do not authenticate an attacker-replaced package and manifest.
- Real Windows clipboard history/cloud exclusion, revealed-password keyboard/context-menu behavior, session lock/disconnect, and suspend/resume require interactive verification. A busy clipboard at application exit can retain a copied password.
- Managed strings and UI controls cannot guarantee complete plaintext erasure from memory, paging, hibernation, or dumps. Clipboard-reading malware is outside the protection provided here.
- Vault filesystem permissions are inherited. The sidecar lease and content-hash check are cooperative conflict protection, not protection against malicious filesystem races or alternate path aliases. Older valid files can be replayed; rollback detection is absent.
- Actual power loss, real disk exhaustion, clean-machine installation, and minimum-hardware key-derivation calibration remain open as recorded in `PRODUCTION_READINESS.md`.
- Graphify was unavailable and no existing graph was present; this review used direct source inspection. The unrelated untracked `DragonBreak/` project was excluded.

Verdict: two security gaps fixed locally, with the above validation and remaining limits. This is not certification that the application is vulnerability-free or ready for production credentials.
