# RDP Vault

RDP Vault stores your Remote Desktop connections in a single encrypted file, launches them through Windows' own `mstsc.exe`, and cleans up the traces Windows leaves behind afterwards.

It is one self-contained `.exe`. Nothing to install first, no .NET runtime required.

## Download

**[Download RDP Vault (RDPVault.exe)](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.exe)**

That link always resolves to the newest release. There is exactly one download — the same `.exe` is the installer, the portable app and the uninstaller.

## What it does

- **One encrypted vault.** Everything lives in `vault.rdpv`: AES-256-GCM, with the key derived from your master password by Argon2id (64 MiB, 3 passes). Without the password the file is noise.
- **Passwords never touch disk in the clear.** When you connect, the password is handed to Windows Credential Manager as a *session* credential and deleted the moment the Remote Desktop window closes. It is never written into the generated `.rdp` file.
- **Recovery Code.** When you create a vault you get a 52-character recovery code. Write it down. It is the only other way in if you forget your master password — and it is shown once, because it is not stored anywhere readable.
- **Windows Hello quick unlock.** Optional, per PC. The key is created and held by that machine's TPM; the vault key is derived from a TPM signature, so it never leaves the hardware. Enrollment verifies the signature is reproducible before trusting it, and refuses rather than silently creating a quick unlock that could never work.
- **Trace cleaning.** After each session, and on lock, RDP Vault removes the Remote Desktop registry history, `Default.rdp`, jump-list and Recent-items entries, and its own temporary files. By default it only removes entries that mention a host stored in *your vault* — your own separate Remote Desktop history is left alone. Settings has an opt-in "clean everything" mode that also clears UserAssist, Prefetch and every saved `TERMSRV/*` credential.
- **Auto-lock.** The vault re-locks after a period of inactivity (60 minutes by default). Open Remote Desktop windows are never closed by this.
- **Removable-drive safety.** If you run it from a USB stick and pull the stick, the vault locks, optionally closes the open sessions, and the app exits.
- **Desktop shortcuts.** One click per connection. These are ordinary Windows `.lnk` shortcuts pointing at `RDPVault.exe --launch <id>`; they contain no host name.
- **Optional self-destruct.** Off by default. Repeated wrong passwords are always slowed down with an escalating delay, which is the real protection. If you deliberately arm self-destruct, the vault is erased after the limit you set — you have to save a Recovery Code and type `ERASE` to turn it on.

## Install, portable, uninstall

Run the downloaded `RDPVault.exe`:

- **Install to this PC** — copies itself to `%LocalAppData%\RDPVault`, creates Desktop and Start Menu shortcuts, registers the `.rdpvlink` file type, and adds an entry to Programs and Features.
- **Run portably** — keep the `.exe` and its `vault.rdpv` side by side on a USB stick. Nothing is written to the host PC outside the temporary files it cleans up itself.

If a `vault.rdpv` already sits next to the `.exe`, it opens straight into that vault instead of offering to install.

**Uninstalling keeps your vault.** Removing RDP Vault through Programs and Features copies `vault.rdpv` (and its automatic backup) to `Documents\RDP Vault Backups` before deleting anything, and tells you where it went. Erasing the vault is a separate, clearly labelled choice that requires typing `ERASE`. A silent uninstall always keeps the vault.

## Backups

Every save writes `vault.rdpv.bak` next to the vault — the previous good copy. Copy `vault.rdpv` somewhere safe anyway. Losing both the file and your Recovery Code means losing the contents; there is no reset and no support line.

## What it does not protect against

Being straight about the limits:

- Anyone using your unlocked Windows session on a PC where you enabled Windows Hello can open the vault. Lock your screen.
- Memory forensics against a running, unlocked instance. The master key is wiped on lock, but connection passwords held as .NET strings cannot be reliably erased from memory.
- Windows Event Logs, EDR/telemetry records of `mstsc.exe` running, and anything logged on the *remote* server. RDP Vault does not touch those — clearing them needs admin rights and is conspicuous in itself.
- Recovery of deleted files by forensic carving. Deleting is not shredding, and on SSDs even overwriting is not a guarantee.
- BitLocker. RDP Vault checks whether the drive holding the vault is encrypted and warns you if it is not. It does not, and cannot, encrypt the drive for you, and it will not refuse to open your own vault.

## Build from source

Requires the .NET 9 SDK on Windows.

```
build.cmd              rem clean, publish, then replace the GitHub release
build.cmd --no-publish rem clean and publish only
```

The output is a single file: `compiled\RDPVault.exe`. Publishing deletes every previous release and tag, creates one new release tagged `vYYYY.MM.DD` with that one `.exe` attached, and verifies the uploaded asset's SHA-256 matches the file that was just built.

## Technical summary

| | |
|---|---|
| Vault file | `vault.rdpv` — JSON envelope, format V2, atomic save with rolling `.bak` |
| Cipher | AES-256-GCM, 12-byte nonce, 16-byte tag |
| Key derivation | Argon2id v1.3, 64 MiB, 3 iterations, 4 lanes, 32-byte salt |
| Recovery Code | 256-bit secret, 52 Crockford-Base32 characters, wrapped over the master key |
| Quick unlock | TPM signature → Argon2id → AES-GCM seal, bound to machine + Windows account |
| Runtime | .NET 9, Avalonia UI, self-contained single-file `win-x64` build |

Detailed design notes live in `project_structure.txt`.
