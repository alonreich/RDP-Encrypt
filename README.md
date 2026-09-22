# RDP Vault

RDP Vault stores your Remote Desktop connections in a single encrypted file, launches them through Windows' own `mstsc.exe`, and cleans up the traces Windows leaves behind afterwards.

It is one self-contained `.exe`. Nothing to install first, no .NET runtime required.

## Downloads

- **[Download RDP Vault for Windows (RDPVault.exe)](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.exe)**  
  *Self-contained single executable for Windows 10/11 x64. Portable, installer, and uninstaller in one binary.*

- **[Download RDP Vault for Android (RDPVault.apk)](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.apk)**  
  *Android mobile package with hardware StrongBox Keystore security and screen-flip session continuity.*

Both links always resolve to the newest release online.

## What it does

- **One encrypted vault.** Everything lives in `vault.rdpv`: AES-256-GCM, with the key derived from your master password by Argon2id (64 MiB, 3 passes). Without the password the file is noise.
- **Passwords never touch disk in the clear.** When you connect, the password is handed to Windows Credential Manager as a *session* credential and deleted the moment the Remote Desktop window closes. It is never written into the generated `.rdp` file.
- **Recovery Code.** When you create a vault you get a 52-character recovery code. Write it down — on paper. It is the only other way in if you forget your master password, and it is shown once, because it is not stored anywhere readable. You cannot dismiss that screen until you confirm you have saved it. There is a Copy button for convenience; it clears the clipboard again after 60 seconds, though Windows clipboard history (Win+V) may still hold a copy. RDP Vault will not write the code to a file for you — a second master key sitting in your Documents folder defeats the point.
- **If you forget your master password**, use *I forgot my master password* on the lock screen and enter the Recovery Code. RDP Vault then takes you straight to setting a new master password, and does **not** ask for the old one — you obviously do not have it.
- **Windows Hello quick unlock.** Optional, per PC. The key is created and held by that machine's TPM; the vault key is derived from a TPM signature, so it never leaves the hardware. Enrollment verifies the signature is reproducible before trusting it, and refuses rather than silently creating a quick unlock that could never work.
- **Trace cleaning.** After each session, and on lock, RDP Vault removes the Remote Desktop registry history, `Default.rdp`, jump-list and Recent-items entries, and its own temporary files. By default it only removes entries that mention a host stored in *your vault* — your own separate Remote Desktop history is left alone. If any file is locked by Windows Explorer, RDP Vault reports it honestly and queues it for the next sweep. Settings has an opt-in "clean everything" mode that also clears UserAssist, Prefetch and every saved `TERMSRV/*` credential.
- **Auto-lock.** The vault re-locks after a period of inactivity (60 minutes by default). Open Remote Desktop windows are never closed by this.
- **Removable-drive safety.** If you run it from a USB stick and pull the stick, the vault locks, optionally closes the open sessions, and the app exits. The drive has to stay missing for about six seconds first, so a momentary hiccup — an antivirus scan, waking from sleep — does not drop your open desktops.
- **Server identity is checked by default.** Windows verifies host certificates by default (`authentication level: 2`). RDP Vault remembers your answer *inside the encrypted vault* and replays it, so you are asked once per host rather than on every connection, and nothing is left on the PC. If a host's certificate ever changes you are warned again — which is how you find out something is impersonating it. You can turn the check off per profile, but then your saved password is sent to whatever answers at that address.
- **Tactical connection overlay & Wake-on-LAN.** Shows a real-time progress overlay during unlock and connection sequences. When Wake-on-LAN is enabled, a live countdown with an instant Cancel button tracks remote host initialization so you are never left waiting in the dark.
- **Desktop shortcuts.** One click per connection. These are ordinary Windows `.lnk` shortcuts pointing at `RDPVault.exe --launch <id>`; they contain no host name.
- **Optional self-destruct.** Off by default. Repeated wrong passwords are always slowed down with an escalating delay, which is the real protection. If you deliberately arm self-destruct, the vault is erased after the limit you set — you have to save a Recovery Code and type `ERASE` to turn it on.

## Install, portable, uninstall

Run the downloaded `RDPVault.exe`:

- **Install to this PC** — copies itself to `%LocalAppData%\RDPVault`, creates Desktop and Start Menu shortcuts, registers the `.rdpvlink` file type, and adds an entry to Programs and Features.
- **Run portably** — keep the `.exe` and its `vault.rdpv` side by side on a USB stick. Nothing is written to the host PC outside the temporary files it cleans up itself. Standard portable execution never requires administrator rights.

If a `vault.rdpv` already sits next to the `.exe`, it opens straight into that vault instead of offering to install.

**Uninstalling keeps your vault.** Removing RDP Vault through Programs and Features copies `vault.rdpv` (and its automatic backup) to `Documents\RDP Vault Backups` before deleting anything, and tells you where it went. Erasing the vault is a separate, clearly labelled choice that requires typing `ERASE`. A silent uninstall always keeps the vault.

## Backups

Every save writes `vault.rdpv.bak` next to the vault — the previous good copy. Copy `vault.rdpv` somewhere safe anyway. Losing both the file and your Recovery Code means losing the contents; there is no reset and no support line.

On Android the vault lives inside the app's private storage, which **Android deletes when the app is uninstalled** or the phone is reset. Use **Settings → Vault Backup → Share backup** to send the encrypted file to your PC or cloud drive. The backup is still encrypted; it is useless to anyone without your master password.

## What it does not protect against

Being straight about the limits:

- Anyone using your unlocked Windows session on a PC where you enabled Windows Hello can open the vault. Lock your screen.
- Memory forensics against a running, unlocked instance. The master key is wiped on lock, and profile passwords in RAM are shielded with ephemeral AES-256-GCM session encryption (`VaultMemoryGuard`) rather than cleartext strings. However, unmanaged memory during active connection setup may still be vulnerable to aggressive kernel-level inspection.
- Windows Event Logs, EDR/telemetry records of `mstsc.exe` running, and anything logged on the *remote* server. RDP Vault does not touch those — clearing them needs admin rights and is conspicuous in itself.
- Recovery of deleted files by forensic carving. Deleting is not shredding, and on SSDs even overwriting is not a guarantee.
- On Android, RDP Vault cannot disconnect your remote session for you. Android forbids one app terminating another app's session, so "End Session" only stops RDP Vault tracking it — you sign out inside Remote Desktop. The app says so rather than pretending otherwise.
- Once a connection is handed to Microsoft Remote Desktop, what that app does with the session, its logs and its own stored state is outside RDP Vault's control.
- Aggressive vendor battery managers (Xiaomi MIUI, Samsung OneUI power saving) can kill the background process and drop the ongoing notification while a session is still running in the other app. Exempt RDP Vault from battery optimisation if that happens.
- BitLocker. RDP Vault checks whether the drive holding the vault is encrypted and warns you if it is not. It does not, and cannot, encrypt the drive for you, and it will not refuse to open your own vault.

## Android Companion (Parallel Distribution Branch)

RDP Vault provides an Android mobile APK companion sharing the exact same cryptographic core and `vault.rdpv` file format. Requires **Android 9 (API 28) or newer**, and a separate RDP client app (Microsoft Remote Desktop is free; the app offers to install it):

- **Mobile TPM Equivalent**: Master keys are sealed inside the phone's hardware Secure Element (StrongBox Keymaster) or ARM TrustZone TEE and are released only by a Class 3 strong biometric, bound through `BiometricPrompt.CryptoObject`. They cannot be exported, even from a rooted device. Adding a new fingerprint to the phone cancels the seal on purpose — unlock once with your master password and the app offers to rebuild it.
- **It hands off, it does not host**: RDP Vault stores and protects your connections, then opens them in Microsoft Remote Desktop (or aRDP) through a standard Android `rdp://` intent. It contains no RDP protocol stack of its own, so it cannot — and never claims to — connect or disconnect a session itself. The in-app banner says "handed off", and tells you when the remote PC stops responding.
- **Passwords stay inside the vault**: a saved remote password is visible only while you are editing that specific connection. Nothing else in the app can show, copy, export or share it, and it is never placed on the Android clipboard. Auto-Type (an optional accessibility service scoped to RDP client apps only) types it straight into Remote Desktop and wipes it from memory immediately; if that fails, the app tells you to read it under Edit and type it yourself.
- **Locks like a vault should**: locks after a configurable idle timeout — on screen as well as in the background — and, by default, the instant the app leaves the screen. Screenshots, screen recording and the app-switcher preview are blocked by default. A running remote session never triggers a lock.
- **Checks before it switches apps**: a one-second reachability check tells you "your PC is asleep" in plain words, instead of leaving Remote Desktop to spin for thirty seconds and emit `0x204`. Offers to send a Wake-on-LAN signal right there — and says so honestly when you are on mobile data, where Wake-on-LAN cannot work.
- **Resolution & Multi-Monitor Protection**: global and per-connection presets (1080p, 720p, 900p, 1440p, 4K, phone-native, or custom), with a single-monitor lock that stops the remote Windows host rearranging desktop icons across secondary displays. "Keep native size" preserves the real desktop resolution and lets you scroll, rather than squashing it into the phone's aspect ratio.
- **Your vault lives only on the phone**: uninstalling the app deletes it. Settings has a one-tap **Share backup** (Quick Share, Drive, email) of the still-encrypted vault, and the app reminds you when there are unbacked-up changes.

## Android Installation & Zero-Clipboard Setup Guide

### Method A: Fast 1-Click Install via ADB (Recommended — Zero Restrictions)
If your phone is connected to your PC with USB debugging enabled, this method installs the app and activates Auto-Type without any grayed-out menus or permission hurdles:

```powershell
# 1. Install the APK
adb install -r compiled\RDPVault.apk

# 2. Grant Auto-Type accessibility permission directly
adb shell settings put secure enabled_accessibility_services com.rdpvault.app/com.rdpvault.app.services.RdpAutoTypeService
adb shell settings put secure accessibility_enabled 1
```

---

### Method B: Manual Sideload via Phone

1. **If Samsung blocks installation ("App blocked to protect your device")**:
   - Go to **Settings → Security and privacy → Auto Blocker** and toggle it **OFF**.
   - If Google Play Protect warns during install, tap **More details → Install anyway**.

2. **Un-grey the Auto-Type switch (Android 13+ "Restricted Settings" bypass)**:
   - In RDP Vault, tap **Settings → Configure** (or tap **Connect** on any profile).
   - In Accessibility, tap **RDP Vault Auto-Type**. When the *"Restricted setting"* pop-up appears, tap **OK**.
   - Return to RDP Vault and tap **Unlock App Info** (or go to phone **Settings → Apps → RDP Vault**).
   - Tap **Force Stop**, then tap the **3 dots (`⋮`)** in the top-right corner.
   - Tap **Allow restricted settings** and confirm with your PIN/fingerprint.
   - Return to **Accessibility → RDP Vault Auto-Type** and toggle the switch **ON**.

Build Android APK:
```
dotnet workload install android
build-apk.cmd
```
The output is `compiled\RDPVault.apk`.

## Build from source

Requires the .NET 9 SDK on Windows.

```
build.cmd              rem clean, publish, then replace the GitHub release
build.cmd --no-publish rem clean and publish only
```

The build outputs are `compiled\RDPVault.exe` (Windows desktop) and `compiled\RDPVault.apk` (Android mobile). The automated CI pipeline builds both artifacts on every release commit, tagging `vYYYY.MM.DD` with both assets attached.

## Technical summary

| | |
|---|---|
| Vault file | `vault.rdpv` — JSON envelope, format V2, atomic save with rolling `.bak` |
| Cipher | AES-256-GCM, 12-byte nonce, 16-byte tag |
| Key derivation | Argon2id v1.3, 64 MiB, 3 iterations, 4 lanes, 32-byte salt |
| Recovery Code | 256-bit secret, 52 Crockford-Base32 characters, wrapped over the master key |
| Quick unlock (PC) | TPM signature → Argon2id → AES-GCM seal, bound to machine + Windows account |
| Quick unlock (Android) | Android Keystore / StrongBox Keymaster → Class 3 BiometricPrompt (Hardware TEE) |
| Runtime | .NET 9, Avalonia UI, Windows single-file & Android APK |

Detailed design notes live in `project_structure.txt` and `ANDROID_ARCHITECTURE.md`.

