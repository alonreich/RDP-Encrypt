# RDP Vault

RDP Vault stores your Remote Desktop connections in a single encrypted file, launches them through Windows' own `mstsc.exe`, and cleans up the traces Windows leaves behind afterwards.

It is one self-contained `.exe`. Nothing to install first, no .NET runtime required.

## Downloads

- **[Download RDP Vault for Windows (RDPVault.exe)](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.exe)**  
  *Self-contained single executable for Windows 10/11 x64. Portable, installer, and uninstaller in one binary.*

- **[Download RDP Vault for Android (RDPVault.apk)](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.apk)**  
  *Android mobile package with embedded native FreeRDP engine, hardware StrongBox Keystore security, virtual trackpad, and screen-flip continuity.*

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
- **Tactical connection overlay, Wake-on-LAN & Stage Skips.** Shows a real-time progress overlay during unlock and connection sequences with live countdowns and instant stage skip buttons (`SKIP WAKE-ON-LAN`, `SKIP PORT KNOCKING`). You can advance immediately if the target machine is already awake or whitelisted without aborting the sequence.
- **Wake-on-LAN First, Stealth Port Knocking Second.** Dispatches Wake-on-LAN magic packets first to boot physical hosts (via local broadcast or cellular WAN unicast), followed immediately by configurable TCP port knocks or ICMP magic payloads to dynamically whitelist the client IP on hardened perimeter firewalls (such as MikroTik RouterOS `action=add-src-to-address-list`) right as the machine starts up.
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
- Aggressive vendor battery managers (Xiaomi MIUI, Samsung OneUI power saving) can kill background processes and drop ongoing notifications if the app is placed in deep sleep. Exempt RDP Vault from battery optimisation if that happens.
- BitLocker. RDP Vault checks whether the drive holding the vault is encrypted and warns you if it is not. It does not, and cannot, encrypt the drive for you, and it will not refuse to open your own vault.

## Android Companion (Embedded FreeRDP Engine)

RDP Vault provides an Android mobile companion sharing the exact same cryptographic core and `vault.rdpv` file format. Requires **Android 9 (API 28) or newer**.

In version 2.0.0, RDP Vault incorporates a **fully embedded, self-contained native FreeRDP engine** (`aFreeRDP` for ARM64 and x86_64), eliminating external RDP client dependencies (Microsoft Remote Desktop / aRDP) and completely removing the need for Android Accessibility Services or ADB configurations:

- **Fully Embedded Native Engine**: End-to-end RDP connection management, NLA credential negotiation, hardware-accelerated GDI framebuffer rendering, and touch/keyboard input run entirely inside RDP Vault.
- **Zero ADB / Zero Accessibility Requirement**: Installs cleanly via standard Android package installers on Android 9 through Android 15. Requires no Accessibility Service permissions, never triggers Android 13-15 "Restricted Settings", and requires no USB debugging or ADB commands.
- **Zero Cleartext Credentials on Disk / Zero Clipboard Exposure**: Passwords remain encrypted in RAM via `VaultMemoryGuard` until the exact moment of connection, decrypting directly in memory into FreeRDP's native NLA structures and wiped immediately from RAM (`Array.Clear`). Clipboard redirection defaults to disabled (`redirectclipboard = 0`), preventing remote data leakage to Android clipboards or IME caches.
- **Mobile TPM Equivalent (Hardware Root of Trust)**: Master keys are sealed inside the phone's hardware Secure Element (StrongBox Keymaster) or ARM TrustZone TEE and released exclusively by Class 3 Strong Biometrics (Fingerprint / 3D Face Unlock) bound through `BiometricPrompt.CryptoObject`. Keys cannot be exported even from rooted devices. Adding a new biometric enrollee cancels the seal intentionally, prompting master password verification to re-seal.
- **Mobile Input Controls & Virtual Trackpad**:
  - **Relative Virtual Trackpad (Default)**: Phone screen functions as a precision laptop trackpad controlling an on-screen mouse pointer with smooth acceleration. Single tap for Left Click, two-finger tap for Right Click, and tap-and-drag for selecting text or moving windows.
  - **One-Shot Click Flipper**: Top toolbar button (`[🖱 Left / Right Click]`) switches to an armed Right Click state in amber; the next tap/click executes a right-click and auto-reverts to Left Click.
  - **Direct Touch Mode**: Tap directly on remote UI elements with instant coordinate mapping.
  - **Smart-Collapsing Toolbar**: Top controls (`[Mode]` → `[Click Flipper]` → `[Keyboard]` → `[Keys]` → `[Disconnect]`) auto-collapse after 3.5s into a sleek top mini-tab (`[ ☰ ]`) to avoid blocking remote desktop windows.
  - **Zoomed-In Edge Auto-Scrolling**: When zoomed in (`> 1.05x`), moving the pointer within 28dp of any screen edge smoothly pans the desktop, strictly stopping at physical remote boundaries.
  - **Soft Keyboard & Modifier Drawer**: Integrated soft keyboard with an expandable modifier drawer providing one-tap access to Ctrl, Alt, Shift, Win, Esc, Tab, Enter, Backspace, Delete, Ctrl+Alt+Del, and F1–F12 keys.
  - **Physical Mouse & Keyboard Forwarding**: Bluetooth and USB mice (left, right, middle click, scroll wheel) and physical keyboards forward directly 1:1 to the remote machine.
  - **Pinch-to-Zoom & Pan**: Smooth two-finger pinch-zoom and pan navigation across the complete remote desktop.
  - **Disconnect Confirmation Guard**: Android Back gesture presents a confirmation dialog to prevent accidental session drops.
  - **Haptic Feedback**: Crisp tactile response on mouse clicks and modifier drawer toggles.
- **Host Desktop Layout Protection**: Locks remote desktop dimensions to native resolution with dynamic resize PDUs disabled (`dynamic resolution = 0`). Connecting from mobile will never squash application windows or scramble multi-monitor desktop icons.
- **Sensor Orientation Freedom & TextureView Rendering**: Hardware-accelerated `TextureView` rendering with full screen rotation freedom (`ScreenOrientation.Sensor`), adapting in-place to portrait or landscape holding positions without aspect distortion or buffer desynchronization.
- **Wake-on-LAN First, Stealth Port Knocking Second**: Dispatches Wake-on-LAN magic packets first, followed by raw TCP SYN and HTTP GET port knocking to dynamically open perimeter firewall rules (e.g. MikroTik `action=add-src-to-address-list`). Interactive skip buttons (`SKIP WAKE-ON-LAN`, `SKIP PORT KNOCKING`) allow skipping stages at any time.
- **Zero False-Positive Notification & Biometric Session Resume**: Ongoing foreground notification appears strictly after FreeRDP connection succeeds (`OnSessionConnected`). Tapping notification requires biometric (fingerprint/3D face) vault authentication before resuming the session. Tapping `[Disconnect]` immediately terminates the connection without unlocking the vault.
- **Vault Portability & Backup**: Open or save the same `vault.rdpv` file used on Windows. Settings includes a one-tap **Share backup** (Quick Share, Drive, email) of the encrypted vault file.

## Android Installation & Quick Start Guide

### Direct Phone Installation

Download **[`RDPVault.apk`](https://github.com/alonreich/RDP-Encrypt/releases/latest/download/RDPVault.apk)** directly to your Android device and tap the file to install. If prompted by your browser or file manager, allow "Install unknown apps" for that app.

### Host Desktop Scale Protection (Recommended for 1080p / Multi-Monitor Hosts)

When connecting from a high-DPI phone to a Windows PC, Windows may attempt to apply mobile DPI scaling (e.g. 225%), which enlarges fonts and rearranges desktop icons. To ensure your host layout remains completely untouched at native 100% scale (96 DPI), run this once in an elevated PowerShell prompt on the Windows host machine:

<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```powershell
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations" -Name "IgnoreClientDesktopScaleFactor" -Value 1 -Type DWord; Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" -Name "IgnoreClientDesktopScaleFactor" -Value 1 -Type DWord
```

</div>

### In-Session Gestures & Navigation

| Action | Trackpad Mode | Direct Touch Mode |
|---|---|---|
| **Move Cursor** | Drag one finger across screen | Cursor moves to touch location |
| **Left Click** | Single tap | Single tap |
| **Right Click** | Two-finger tap (or arm Click Flipper) | Long press (or arm Click Flipper) |
| **One-Shot Click Flipper** | Tap `[🖱 Left / Right Click]` on top bar to arm Right Click (amber); next tap right-clicks and auto-reverts to Left Click | Same |
| **Drag & Select** | Double-tap and drag | Long press and drag |
| **Scroll Wheel** | Slide one finger along right edge | Two-finger vertical drag |
| **Zoom Canvas** | Two-finger pinch / spread | Two-finger pinch / spread |
| **Pan Canvas** | Two-finger drag (or edge auto-scroll when cursor hits bezel) | Two-finger drag |
| **Edge Auto-Scroll** | When zoomed in, move cursor within 28dp of bezel to auto-pan with physical border clamping | N/A |
| **Smart Toolbar** | Top bar auto-collapses after 3.5s into top mini-tab `[ ☰ ]`; tap tab to expand | Same |
| **Modifier Keys** | Tap `[☰ Keys]` to open drawer for Ctrl, Alt, Shift, Win, Esc, Tab, Ctrl+Alt+Del, F1-F12 | Same |
| **Keyboard** | Tap `[⌨ Keyboard]` to toggle Android soft keyboard | Same |
| **Physical Mouse & Keys** | Native Bluetooth/USB mouse clicks, wheel scrolling, and physical keys forwarded 1:1 | Same |
| **Disconnect Guard** | Back button triggers confirmation dialog: *"Disconnect Remote Desktop?"* | Same |

### Developer Sideloading via ADB (Optional)

If installing from a development PC using bundled ADB:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe install -r compiled\RDPVault.apk
```

</div>

---

### Android Build Prerequisites
To compile the Android APK from source on Windows:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
dotnet workload install android
```

</div>

<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
build-apk.cmd
```

</div>

The output package is placed at `compiled\RDPVault.apk`.

## Build from source

Requires the .NET 9 SDK on Windows.

Build and replace GitHub release:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
build.cmd
```

</div>

Build only (clean and publish locally without touching GitHub releases):
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
build.cmd --no-publish
```

</div>

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
| Android Engine | Embedded native FreeRDP (aFreeRDP), ARM64 / x86_64, TextureView rendering |
| Runtime | .NET 9, Avalonia UI, Windows single-file & Android APK |

Detailed design notes live in `project_structure.txt` and `ANDROID_ARCHITECTURE.md`.

## Third-Party Licenses & Legal Notices

RDP Vault embeds and links open-source software libraries under their respective permissive licenses:

- **FreeRDP & WinPR**: Licensed under the [Apache License, Version 2.0](https://www.apache.org/licenses/LICENSE-2.0). Copyright (c) 2009-2024 FreeRDP Developers. FreeRDP provides the core native Remote Desktop Protocol engine.
- **OpenSSL**: Licensed under the Apache License 2.0 / Dual OpenSSL and SSLeay License. Copyright (c) 1998-2024 The OpenSSL Project.
- **FFmpeg / Libav Native Codecs**: Dynamically linked shared libraries (`libavcodec`, `libavutil`, `libswresample`, `libswscale`) licensed under the [GNU Lesser General Public License (LGPL) v2.1+](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html).
- **Isopoh.Cryptography.Argon2**: Licensed under the MIT License. Copyright (c) 2018 Michael Heyman.
- **Avalonia UI**: Licensed under the MIT License. Copyright (c) 2014-2024 AvaloniaUI OÜ.

For full license texts and legal notices, see [`THIRD_PARTY_LICENSES.md`](THIRD_PARTY_LICENSES.md).

