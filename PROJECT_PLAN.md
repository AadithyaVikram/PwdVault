# PwdVault — Windows MVP plan

Status: initial Windows MVP implemented with .NET 10 and Avalonia 12. Automated verification and Windows startup checks are recorded in README.md; independent security review and the remaining manual release checks are outstanding.

## Product scope

A single-user Windows desktop password vault that works entirely offline. The user creates a vault and a master passphrase, unlocks it to manage entries, and locks or exits when finished. Closing the main window exits the application; there is no system tray mode in the MVP.

Each entry contains a title, username, password, optional URL, and optional notes. All these fields are encrypted at rest. Search runs only against unlocked data in memory.

The vault is portable between Windows computers and does not depend on a particular Windows account or device to decrypt it. No account registration, telemetry, network requests, browser integration, cloud synchronization, biometric unlock, or password recovery is included.

## User flows

1. Create: select a vault location, enter and confirm a master passphrase, explain that forgetting it makes the vault inaccessible, and save the initial encrypted vault.
2. Unlock: choose an existing vault and enter its master passphrase. Do not display any entries until key and payload authentication succeed.
3. Manage: add, edit, delete, and search entries. Passwords are masked unless explicitly revealed. Confirm entry deletion.
4. Generate: create passwords using a cryptographically secure random source, with selectable length and character sets and no biased character selection.
5. Copy: copy a selected password, then clear that copy after 20 seconds if the clipboard has not changed.
6. Lock: provide a visible lock action; lock after five minutes of inactivity in the app, on Windows session lock, and on suspend. The inactivity interval is an initial default, not a previously agreed requirement.
7. Exit: closing the window locks and exits. Preserve the last valid saved vault if any operation fails.
8. Backup and restore: export encrypted vault copies and validate a backup by unlocking it before replacing the active vault. Preserve the current vault before replacement.
9. Change passphrase: require the current passphrase, confirm the new one, and save the protected vault key safely. Explain that older backups retain their older passphrase.

## Security boundary

Primary protections: confidentiality of a copied vault file, detection of unauthorized ciphertext modification, protection against casual access while locked, and prevention of avoidable data loss during saves.

Limits: an attacker controlling the Windows session or application process can capture passwords or keys while in use. The app cannot promise protection from malware, keyloggers, administrator access, screenshots, or all memory remnants. Authenticated encryption does not detect replacement with an older valid vault. No automatic recovery is possible after forgetting the master passphrase.

A strong master passphrase remains essential: an attacker holding a vault copy can guess passwords without using the app's unlock screen.

## Cryptographic design

- Use maintained cryptographic libraries and standard constructions; do not implement cryptographic primitives.
- Derive a 256-bit key-encryption key from the master passphrase using Argon2id with a fresh random salt per vault and after passphrase changes. Benchmark memory and time costs on supported hardware before selecting defaults. Store algorithm identifiers and costs in the versioned header.
- Generate an independent random 256-bit data-encryption key using the operating system's cryptographically secure random source.
- Encrypt the data-encryption key with the derived key using authenticated encryption. Encrypt the complete serialized vault payload with the data-encryption key using AES-256-GCM.
- Use fresh nonces for every encryption under the relevant key, including every save. Enforce the selected library's nonce and tag requirements.
- Authenticate relevant header metadata as associated data. Specify canonical header encoding and the precise associated-data bytes before implementing the file format.
- Persist only the header, encrypted data key, and encrypted payload, with their salts, nonces, and authentication tags. Never persist an unencrypted key or master passphrase.
- Changing the passphrase rewraps the data key with a new salt, derived key, and nonce. This does not revoke access to previously copied vault files or backups, and is not sufficient remediation if the data key itself was compromised.
- Validate file size, field lengths, format version, and reasonable Argon2 resource limits before expensive work. Treat vault files as untrusted input.

No separate login password database is needed. Unlock succeeds only after authenticated key decryption and payload decryption. Authentication failure must not overwrite or reset the vault; the UI should explain that either the password is incorrect or the vault is damaged.

## Windows clipboard behavior

Password copies expire after 20 seconds. Use Windows clipboard change tracking and ownership checks to ensure a later user copy is preserved, even if it contains identical text. Handle clipboard contention without crashing or replacing someone else's content. Protect the check-and-clear operation against intervening clipboard changes.

Mark password copies to opt out of Windows clipboard history and cloud synchronization using supported Windows clipboard formats. Verify these behaviors on supported Windows versions. Other applications and third-party clipboard managers may still capture clipboard content.

On explicit lock or exit, attempt to clear a still-owned password copy immediately. Do not claim cleanup succeeded if Windows refused access. Avoid retaining an extra plaintext password solely for the timer.

## Storage and memory handling

- Serialize plaintext in memory only; do not write plaintext databases, temporary files, exports, logs, or debug traces.
- Save encrypted content through a temporary file in the same directory, flush it, then replace the destination safely while retaining a recoverable previous version. Define and test recovery behavior for failures at each stage.
- Prevent concurrent writers to the same vault. A second instance must not silently overwrite changes.
- Limit file access with Windows permissions where practical, without relying on permissions instead of encryption.
- Avoid automatic writes to a vault that failed authentication or parsing.
- Clear keys and mutable plaintext buffers when locking where feasible; minimize immutable secret strings and document runtime limitations.
- Clear revealed text, search results, and entry views on lock. Never log passwords, passphrases, key material, or decrypted entries.
- Encrypted backups remain sensitive: old copies can retain deleted entries and older passphrases. Do not promise secure erasure from SSDs or backups.

## Implementation milestones

1. Stack selected: C# on .NET 10 LTS with Avalonia 12 and MVVM. Keep Core, Security, and Storage portable, with Windows clipboard/session behavior behind a platform contract. Windows is the first release target; macOS/Linux adapters and packaging come later.
2. Implement and test the vault engine independently of the UI: create, unlock, read, mutate, save, and change passphrase.
3. Build the create/unlock screens and entry management UI, then implement locking and secret lifetime management.
4. Add password generation, Windows clipboard protections, encrypted backup, and restore.
5. Exercise security and data-loss scenarios, package a release, and document installation and remaining limitations. Obtain independent security review before relying on it for important credentials.

## Required verification

- Correct passphrase unlocks; incorrect passphrases disclose no entries and change no file.
- Modified headers, wrapped keys, ciphertext, tags, truncation, invalid lengths, excessive resource parameters, and unsupported versions are rejected safely.
- Repeated saves produce fresh nonces; plaintext entry fields and passphrases do not appear in persisted artifacts.
- Interrupted writes, disk-full errors, permission errors, and simultaneous instances preserve a recoverable valid vault.
- Passphrase changes preserve entries; the previous passphrase fails on the updated vault; old backups still open with their original passphrase.
- Restored backups reproduce the saved entries; failed restore attempts preserve the current vault.
- Session lock, suspend, inactivity, explicit lock, and exit hide entries and release keys as designed.
- Clipboard timeout clears the app's copy, preserves subsequent copies including identical text, and handles contention. Check history and sync exclusion on supported Windows versions.
- Password generation honors selected constraints and uses an appropriate cryptographic random source.

## Design references

- OWASP Cryptographic Storage Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html
- OWASP Password Storage Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html

Current API and dependency documentation must be checked during implementation; these references do not substitute for review of the complete application.
