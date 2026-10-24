# Vault file format, version 1

All integers are signed 32-bit little-endian values. Lengths and resource parameters must pass validation before key derivation or payload allocation. This is an application-specific format and has not received independent security review.

| Offset | Length | Meaning |
| --- | --- | --- |
| 0 | 8 | ASCII magic: PWDVLT01 |
| 8 | 4 | Format version: 1 |
| 12 | 4 | Algorithm suite: 1 = Argon2id v1.3 + AES-256-GCM |
| 16 | 4 | Argon2 memory in KiB; newly created vaults use 65,536 |
| 20 | 4 | Argon2 iterations; newly created vaults use 3 |
| 24 | 4 | Argon2 parallelism; newly created vaults use 1 |
| 28 | 16 | Random Argon2 salt |
| 44 | 16 | Random vault identifier |
| 60 | 12 | Random wrapped-key nonce |
| 72 | 32 | Encrypted 256-bit data key |
| 104 | 16 | Wrapped-key authentication tag |
| 120 | 12 | Random payload nonce |
| 132 | 4 | Encrypted payload length N |
| 136 | N | Encrypted UTF-8 JSON entry array |
| 136 + N | 16 | Payload authentication tag |

The key-encryption key is the 32-byte Argon2id output from the UTF-8 passphrase, without trimming or Unicode normalization. The wrapped data key uses bytes 0 through 59 as its AES-GCM associated data. The payload uses bytes 0 through 135 as its AES-GCM associated data. Each nonce is independently generated using the OS cryptographic random source.

The file must end exactly after the payload tag. The encrypted payload is limited to 8 MiB, 10,000 entries, and JSON depth 8. Each entry contains Id, Title, Username, Password, Url, and Notes. Entry identifiers must be nonempty and unique; title length is 1 to 256 (not whitespace only), username/password/URL at most 4,096 characters each, and notes at most 65,536 characters. Character counts are .NET UTF-16 code-unit counts.

Accepted Argon2 inputs are memory 19,456 to 262,144 KiB, iterations 2 to 10, parallelism 1 to 4, and memory times iterations at most 786,432 KiB-passes. These bounds mitigate malicious resource requests; they do not make weak master passphrases safe. The initial 64 MiB / 3 / 1 settings are provisional, not a hardware calibration system. Unlock timing must be assessed on minimum supported hardware before release.

A passphrase change generates a new salt and wrapping nonce and re-encrypts the data key, followed by a fresh payload encryption. It preserves the data key and vault identifier. Old copies and backups retain their prior passphrase and entries. A stolen old copy is not revoked by changing the passphrase.

The engine authenticates the wrapped key before attempting to decrypt the payload. Entries are exposed only after both tags and payload validation succeed. Authenticated encryption does not detect replay of an older valid file.

Writes use an encrypted temporary file in the destination directory, flush it to disk, and replace the destination while preserving a .previous file. Creation and backup export refuse to overwrite an existing path. A sidecar .lock file is held with exclusive access for the lifetime of an open vault; its presence alone does not indicate that a vault is currently open. Saving also checks the active file hash to detect changes made outside this app. This is cooperative conflict protection, not a defense against a malicious process racing filesystem operations or alternate filesystem aliases.

References:
- [OWASP cryptographic storage](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
- [Argon2 implementation](https://github.com/kmaragon/konscious.security.cryptography)
- [AES-GCM in .NET](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm)
