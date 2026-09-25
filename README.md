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
- **Stealth Port Knocking & WAN Wake-on-LAN.** Bypasses hardened perimeter firewalls (such as MikroTik RouterOS) using configurable TCP port knocks or ICMP magic payloads before dispatching connections. Port knocking executes first to dynamically whitelist the client source IP, allowing subsequent unicast Wake-on-LAN magic packets across cellular/WAN as well as local subnet broadcasts on Wi-Fi.
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
- **Checks before it switches apps**: a pre-flight reachability check tells you "your PC is asleep" in plain words, instead of leaving Remote Desktop to spin for thirty seconds and emit `0x204`. Offers to send a Wake-on-LAN signal right there — supporting both local subnet broadcast on Wi-Fi and unicast Wake-on-LAN across cellular/WAN.
- **Stealth Port Knocking**: Built-in HTTP GET and dual-stack raw TCP SYN knock engine (configurable port and delay) to open dynamic perimeter firewall rules (e.g. MikroTik `action=add-src-to-address-list`) before RDP pre-flight checks and hand-off.
- **Resolution & Multi-Monitor Desktop Protection**: streamlined resolution options (1080p Standard Landscape [Default], Multi-Monitor Spanning, or Custom Resolution), with single-monitor lock and automatic landscape pre-rotation that prevents external RDP clients from negotiating portrait viewport dimensions (e.g., 1080x1920). This guarantees the remote Windows host does not squash open windows or scramble multi-monitor desktop icons, while smart-scrolling (`smart sizing:i:1`) lets you pan and zoom across the full 1080p desktop from your phone. For complete protection in Microsoft Remote Desktop (Windows App), configure **Settings → Display → Orientation: Lock to landscape** and **Display resolution: 1920 x 1080**.
- **Your vault lives only on the phone**: uninstalling the app deletes it. Settings has a one-tap **Share backup** (Quick Share, Drive, email) of the still-encrypted vault, and the app reminds you when there are unbacked-up changes.

## Android Installation & Zero-Clipboard Setup Guide

### Method A: Step-by-Step Installation via ADB (Recommended — Zero Restrictions)

This method sideloads the application directly and activates the secure Auto-Type Accessibility service without dealing with Android 13/14/15's grayed-out "Restricted Settings" menus.

#### Step 1: Prepare the APK File & Working Directory
1. Build or download `RDPVault.apk`:
   - If building from source: run `build-apk.cmd` (or `build.cmd`). The output artifact is generated at `compiled\RDPVault.apk`.
   - If downloading from GitHub Releases: place `RDPVault.apk` into `compiled\RDPVault.apk`.
2. ADB is bundled directly in the repository under `.\adb\`:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); border-radius: 6px; margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe version
```

</div>
   *(Alternatively, add `.\adb` to your PATH or use system-wide ADB).*

#### Step 2: Enable Developer Options & USB Debugging on Your Phone
1. Open phone **Settings** → **About phone** (or **About device** → **Software information**).
2. Locate the **Build number** entry.
3. Tap **Build number** 7 times consecutively until you see the toast message: *"You are now a developer!"* (enter your phone PIN/pattern if prompted).
4. Return to the main **Settings** menu and tap **System → Developer options** (on Samsung devices, **Developer options** appears at the very bottom of the main Settings menu).
5. Scroll down to the **Debugging** section and toggle **USB debugging** to **ON**. Tap **OK** on the confirmation dialog.

#### Step 3: Connect Phone via USB & Authorize PC
1. Connect your phone to your PC using a high-quality USB data cable (ensure it is not a charge-only cable).
2. If prompted on the phone for USB connection mode, select **Transferring files / Android Auto** (MTP).
3. Look at your phone's screen. A dialog will appear: **"Allow USB debugging?"**.
4. Check the box **"Always allow from this computer"** and tap **Allow**.

#### Step 4: Verify Device Connection
Confirm your phone is authorized and recognized:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe devices
```

</div>

#### Step 5: Sideload & Install APK
Install the freshly built package with update and downgrade flags:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe install -r -d compiled\RDPVault.apk
```

</div>

#### Step 6: Flush Process & Service State
Terminate running instances so Android flushes any dead binder tokens from prior versions:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe shell am force-stop com.rdpvault.app
```

</div>

#### Step 7: Reset Stale Accessibility State
Clear any crashed or suspended accessibility state from prior versions:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe shell settings delete secure enabled_accessibility_services
```

</div>

#### Step 8: Enable Accessibility Subsystem
Activate the Android accessibility subsystem:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe shell settings put secure accessibility_enabled 1
```

</div>

#### Step 9: Register & Bind RDP Vault Auto-Type
Bypass Android restricted settings and bind the service immediately:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe shell settings put secure enabled_accessibility_services com.rdpvault.app/com.rdpvault.app.services.RdpAutoTypeService
```

</div>

#### Step 10: Grant Foreground Notification Permission
Authorize session foreground notifications without runtime prompts:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe shell pm grant com.rdpvault.app android.permission.POST_NOTIFICATIONS
```

</div>

#### Step 11: Launch RDP Vault
Start the application on the phone:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe shell am start -n com.rdpvault.app/com.rdpvault.app.MainActivity
```

</div>

#### Diagnostic Checks (Optional)

Verify that the Auto-Type accessibility service is bound:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe shell dumpsys accessibility | findstr /C:"Bound services"
```

</div>

Stream real-time connection, port knocking, and password injection logs:
<div style="background-color: rgb(35, 35, 35); color: rgb(255, 190, 27); border-radius: 6px; padding: 4px 12px; border-left: 4px solid rgb(255, 190, 27); margin: 6px 0 14px 0;">

```cmd
.\adb\adb.exe logcat -v time -s RDPVault RdpAutoTypeService
```

</div>

---

### Method B: Manual Sideload via Phone (Without PC/ADB)

If you do not have a PC with ADB available, install directly on the phone:

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
| Runtime | .NET 9, Avalonia UI, Windows single-file & Android APK |

Detailed design notes live in `project_structure.txt` and `ANDROID_ARCHITECTURE.md`.

