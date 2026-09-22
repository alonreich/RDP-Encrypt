# RDP Vault Android Companion Architecture

> **Revision 1.2.0 (September 2026 audit).** Section 5 at the end of this document is the
> current, corrected description of how the Android app behaves. Where sections 1-4
> disagree with it, section 5 wins - two statements in section 2 described intended
> behaviour that the 1.1.0 code did not actually implement, and they are flagged inline
> below.

## 1. Executive Summary
RDP Vault Android is a parallel distribution branch providing zero-compromise encrypted remote desktop capabilities on mobile devices. It shares the identical cryptographic core, in-memory credential protection, and JSON vault structure (`vault.rdpv`) as the Windows desktop version.

---

## 2. Key Architecture Pillars

### 2.1 Hardware Security Root of Trust (Mobile TPM Equivalent)
- **Technology**: Android Keystore with StrongBox Keymaster.
- **Hardware Layer**: Runs inside the device's dedicated Hardware Security Module (HSM / Secure Element) on supported devices (Pixel, Samsung Knox), falling back gracefully to the ARM TrustZone Trusted Execution Environment (TEE).
- **Authentication**: Requires Class 3 Strong Biometrics (Fingerprint or 3D Face Unlock) via `BiometricPrompt.CryptoObject`.
  > **CORRECTION (see 5.2):** this was aspirational, not true, in 1.1.0 - the wrap key was generated without `SetUserAuthenticationRequired(true)` and `BiometricPrompt` was never handed a `CryptoObject`. It became true in 1.2.0.
- **Compromise Resistance**: Even if the Android OS is rooted, dumped, or compromised, hardware-bound master keys cannot be exported from the secure element.

### 2.2 Screen Rotation & Session Continuity (Zero Mid-Session Re-Authentication)
- **Problem Solved**: Standard Android apps recreate the `Activity` on orientation flip, destroying in-memory keys and prompting for biometrics mid-session.
- **Solution**:
  1. `AndroidManifest.xml` declares:
     `android:configChanges="orientation|screenSize|screenLayout|smallestScreenSize|uiMode|density"`
     Orientation changes are handled in-place via `OnConfigurationChanged` without destroying the activity.
  2. Session metadata and the hand-off state reside inside a dedicated Android `ForegroundService` (`RdpSessionService`).
     > **CORRECTION (see 5.4):** there is no FreeRDP socket or framebuffer. This app embeds no RDP protocol stack; the session lives inside the external RDP client.
  3. The activity acts solely as a viewport. When flipped from portrait to landscape, the viewport merely adjusts display dimensions while the underlying session remains uninterrupted.
  4. Auto-lock timers are suspended while a foreground session is active.

### 2.3 Mobile RDP Client Intent Handoff & Strict Credential Isolation
- **Architecture**: `RdpLauncher` dispatches connections via standard Android RDP Intent handoff (`rdp://`).
- **Target Clients**: Microsoft Remote Desktop / Windows App (`com.microsoft.rdc.androidx`, `com.microsoft.rdc.android`), Free aRDP (`com.iiordanov.freeaRDP`), and aRDP Pro (`com.iiordanov.aRDP`).
- **Store Fallback**: Direct one-tap redirect to Google Play Store if no client is installed.
- **Zero Clipboard Exposure**: Passwords NEVER touch the system clipboard, eliminating credential leakage to predictive keyboards, clipboard logging services, or cloud sync.
- **Certificate Warning Suppression**: Automatically configures `authentication level=i:0` and `promptcredentialonce=i:1` to suppress untrusted certificate and server identity warnings.
- **Network Resilience**: Dynamic DNS resolution and dual-endpoint WOL (broadcast + unicast across WOL port and custom RDP port).

### 2.4 Vault Interchangeability
- The Android app directly opens, imports, and updates the exact same `vault.rdpv` file used on Windows.
- Android seals register hardware-bound entries in the `Seals` list, allowing identical vaults to move seamlessly between PC and phone.

---

## 3. Project Map & Build Pipeline

```
C:\Full_Control\RDP_Encrypt\
├── RDPVault.Core\                (Shared .NET 9 BCL library)
│   ├── AppPaths.cs
│   ├── Crypto.cs                 (Argon2id + AES-256-GCM)
│   ├── Models.cs                 (RdpProfile, VaultSettings, VaultMemoryGuard)
│   └── RecoveryKeyGenerator.cs   (Crockford-32 code generator)
├── RDPVault\                     (Windows Desktop App - compiled\RDPVault.exe)
├── RDPVault.Android\             (Android Companion - compiled\RDPVault.apk)
│   ├── AndroidManifest.xml
│   ├── MainActivity.cs
│   ├── Services\RdpSessionService.cs
│   ├── Security\AndroidHardwareKeyStore.cs
│   ├── Rdp\FreeRdpClient.cs
│   └── Rdp\RdpLauncher.cs
├── build.cmd                     (Builds & publishes Windows RDPVault.exe)
└── build-apk.cmd                 (Builds & publishes Android RDPVault.apk)
```

### Local Build Requirements
To compile the APK locally:
1. Run `dotnet workload install android`
2. Run `.\build-apk.cmd`

---

## 4. UI/UX & Technical Hardening (September 2026 Release)

### 4.1 UI/UX Critical Improvements
1. **52-Character Crockford-Base32 Recovery Code**: Emergency recovery displays and unlock inputs use the full 52-character Crockford-Base32 format (`RecoveryCode.Normalize` / `RecoveryCode.Format`), resolving earlier 24-character truncation failures.
2. **Unsaved Profile Discard Protection**: Back button and Cancel triggers prompt `OverlayConfirmDiscardProfile` whenever unsaved edits exist in the connection editor.
3. **RD Gateway Configuration & Handoff**: `TxtProfileGateway` is labeled "RD GATEWAY SERVER (OPTIONAL)" and passes `gatewayhostname`, `gatewayusagemethod=1`, and `gatewayprofileusagemethod=1` in the launch intent.
4. **Dedicated Status/Notice Feedback**: Emerald `TxtLockNotice` (`#2FBF71`) is separated from red `TxtLockError` (`#E83030`) on the lock screen.
5. **Primary Accent Unlock CTA**: Prominent `#005FB8` styling on `BtnUnlock`.
6. **Safe-Area Notch / Status Bar Margins**: 52-56px top padding across all views guarantees zero camera punch-hole occlusion.
7. **Active Remote Session Banner Redesign**: Vertical card layout with equal-width Resume/End action buttons. *(The "✕ Dismiss" control was removed again in 1.2.0 - see 5.3.)*
8. **In-Place Vault Restore Confirmation**: `OverlayConfirmRestoreVault` safeguards against accidental vault overwrites during backup imports, and files standardize on `.rdpv`.
9. **Collapsible Wake-on-LAN**: `PnlWolDetails` expands only when WOL is enabled, and enforces MAC address normalization via `MacAddressHelper.TryNormalizeMac`.
10. **Configurable Auto-Lock**: `CmbSettingsAutoLock` dropdown provides 1, 5, 15, 30, 60 minutes, or Never timeouts.

### 4.2 Architectural Findings Resolved
- **Finding A (Native FreeRDP Cleanup)**: Obsolete native FreeRDP P/Invoke stubs and dead `PanelSession` removed.
- **Finding B (Sensitive Clipboard & Resume Auto-Wipe)**: Copied passwords and recovery keys are flagged with `android.content.extra.IS_SENSITIVE` on Android 13+ and automatically purged via `CheckAndWipeExpiredClipboard()` on app resume.
- **Finding C (Foreground Service & Android 14+ Permissions)**: Service declared as `connectedDevice` with `POST_NOTIFICATIONS` runtime permission request.
- **Finding D (Intent Handoff Parameter Integrity)**: Parameterized `rdp://` URI handoff with fallback to Google Play Store.
- **Finding E (Search Debounce & Status Feedback)**: 150ms debounce on search queries with distinct feedback for empty vault vs no matching query.

---

## 5. September 2026 Audit — Release 1.2.0 (supersedes contradicting statements above)

> Where this section disagrees with sections 1–4, **this section is correct**. Sections 2.1
> and 2.2 in particular described intent rather than shipped behaviour; see 5.2 and 5.4.
> The authoritative, numbered record lives in `project_structure.txt` **SECTION 19**.

### 5.1 Stored remote passwords are deliberately unviewable outside the profile editor

**This is a product decision, not an oversight. Do not "fix" it.**

- A connection password can be entered, changed and revealed **only while editing that
  connection** (`BtnToggleProfilePass`, inside `PanelProfileEditor`).
- Nowhere else may a stored password be shown, copied, exported, logged or placed on the
  clipboard — not on connection cards, not in the launch overlay, not in Settings, not in a
  notification, not in logcat. There is no "copy password" control anywhere in the app.
- The only automated path out of the vault is `RdpAutoTypeService`, which holds the
  credential in volatile memory for at most 45 seconds and wipes it on injection.

Rationale: once a password is set there is no routine reason to look at it, and every
viewing surface is another place it can leak — a shoulder, a screenshot, a screen recorder,
a clipboard monitor, a keyboard history cache. Confining the reveal to explicit edit mode
leaves exactly one place the secret can surface, and reaching it already requires an
unlocked vault plus deliberate navigation into that specific profile.

Accepted consequence: when Auto-Type cannot find Remote Desktop's password field, the user
must open **Edit** on that connection, read the password there, and type it. The launch
overlay and the Auto-Type failure notification now say exactly that.

### 5.2 Biometrics are now cryptographically binding (was decorative)

`AndroidHardwareKeyStore.GenerateHardwareKey()` previously accepted a `requireBiometrics`
parameter **and ignored it**: it never called `SetUserAuthenticationRequired(true)`, and
`BiometricPrompt` was shown as an unrelated event before a Cipher was obtained separately.
The master key could be unsealed with no biometric at all, which made every claim in §2.1
false in code.

Now:

- Keys are created with `SetUserAuthenticationRequired(true)`,
  `SetUserAuthenticationValidityDurationSeconds(-1)` (authorise **every** use, strong
  biometric only) and `SetInvalidatedByBiometricEnrollment(true)`.
- Every seal and unseal runs through `BiometricPrompt.CryptoObject`;
  `EnrollAndSealAsync()` / `AuthenticateAndUnsealAsync()` are the only supported entry
  points, and the Cipher that performs `DoFinal` is the one the Keymaster released for that
  authorisation.
- Enrolling a new fingerprint now **destroys** the seal by design rather than silently
  extending vault access to the new finger. The app detects that state and asks the user
  whether to rebuild it (`OverlayRepairBiometrics`) — it never repairs silently.

### 5.3 The app never claims to own the remote session

Android sandboxing forbids one app disconnecting another app's session. Accordingly:

- The banner reads **"HANDED OFF TO REMOTE DESKTOP"**, never "connected".
- "End Session" opens an explainer offering *Open Remote Desktop and disconnect there* or
  *Just stop tracking it here*; the notification action is "Stop tracking".
- `RdpSessionService` TCP-probes the target every 20s and reports **"not responding"** in
  the notification and the banner (which turns red) — a reachability fact, not a claim about
  the RDP session itself.
- The `✕ Dismiss` control is gone: while a hand-off is tracked, the banner stays, so there is
  always a one-tap route back.

### 5.4 No RDP protocol stack exists in this app

§2.2 claimed the `ForegroundService` hosts "the active FreeRDP socket connection,
framebuffer, and memory-protected credentials". Untrue. RDP Vault for Android embeds **no**
RDP stack, no native library and no socket-level implementation. Every session is an Android
`rdp://` VIEW Intent handed to an external, user-installed client. `Rdp/FreeRdpClient.cs`
now declares no types at all; do not reintroduce P/Invoke bindings there.

### 5.5 Locking, privacy and network honesty

- **Real auto-lock**: a foreground idle timer (fed by tunnelled input events and
  `Activity.OnUserInteraction`) locks the vault while the app is open and untouched — the
  old timer only ever ran in the background. New vaults default to 1 minute plus
  "lock the instant the app leaves the screen". Never fires during a connect or while a
  hand-off is tracked; file pickers and the share sheet are exempted via
  `BeginExternalActivity()`.
- **FLAG_SECURE** blanks the app-switcher thumbnail and blocks screenshots and screen
  recording, with an explicit opt-out in Settings. `allowBackup=false` plus
  `data_extraction_rules.xml` forbid cloud backup and device-to-device transfer.
- **Pre-flight reachability**: a 1.5s TCP connect before hand-off, so an asleep PC is
  reported in plain words instead of Remote Desktop spinning into `0x204`. Offers
  *Send Wake-on-LAN* / *Check again* / *Connect anyway*.
- **Wake-on-LAN** now targets the subnet-directed broadcast address (many OEM builds drop
  `255.255.255.255`), no longer fires meaningless packets at the host's RDP port, and
  refuses — out loud — to pretend it can wake a PC over mobile data.
- **Import validation**: candidate bytes are parsed as a `VaultFile` with a KDF salt and
  Wrap/Data blobs before anything overwrites a live vault. The old check was "longer than
  16 bytes".
- **Backup reminder**: `vault.rdpv` lives in `FilesDir`, which Android destroys on
  uninstall, so the connections list warns whenever the vault has changed since the last
  export. Settings leads with **Share backup** through a scoped `FileProvider`.
- **minSdkVersion 28** (Android 9). StrongBox, Class 3 `BiometricPrompt` and
  `ClearPrimaryClip` all require API 28; API 26–27 devices could previously install and hit
  degraded paths.

### 5.6 Interface corrections

| Was | Now |
|---|---|
| "Keep Native 1920x1080" regardless of the chosen resolution | Label and hint rewritten live from the actual selection, including Custom and "match phone screen" |
| Recovery-code caret jumped to position 52 on every keystroke | Caret preserved by counting significant (non-hyphen) characters, mirroring `RecoveryCode.Normalize` exactly |
| Auto-Type wall of text on every single connect | Asked once, with "Don't ask me again"; adb line removed from user-facing copy; re-armable from Settings |
| Host, port, user, WOL and resolution crammed into one ellipsised line | Wrapping chip row: resolution, ⚡ Wake-on-LAN, 🌐 RD Gateway, 🔑 password state |
| Duplicate Save/Cancel at top *and* bottom of the editor, over 400px of dead space | One Save and one Cancel in the sticky header; essentials visible, rest behind "▸ Advanced options"; form ends on Delete |
| Hardcoded 320px / 520px scroll jumps | `BringIntoView()` on focus — correct on any screen size or keyboard height |
| Clipboard was the only way to save a recovery code; Back skipped the confirmation | Save as file, Share as text, or copy; Back is trapped until the box is ticked |
| Opening Settings silently rewrote the user's scaling setting | `_suppressSettingsSave` guards population; settings are written only on real input |
| Fixed `Height="44"`, 10–11px labels | `MinHeight`, 12px floor, wrapping labels, `ConfigChanges.FontScale` |

### 5.7 Build status — built and verified (2026-09-22)

Built with .NET SDK 9.0.318 + android workload 35.0.105, JDK 21, Android SDK platform 35 /
build-tools 35.0.0. **0 errors, 0 C# warnings.** Avalonia XAML compiles, which proves all
178 named controls in `MainView.axaml` resolve against the code-behind. Every AndroidX
binding flagged as risky in the previous revision compiles as written.

Artifact: `compiled\RDPVault.apk`, 41 MiB, versionCode 2 / versionName 1.2.0,
SHA-256 `233d609d10ac3999ab48d3e73754a18de072c392724f21849108cb114ac7b3dc`.
apksigner reports v1 + v2 + v3 all valid, `zipalign -c 4` OK, ABIs arm64-v8a /
armeabi-v7a / x86_64. Merged manifest verified: minSdk 28, target 35, FileProvider
authority, `connectedDevice` foreground service type, accessibility service intent-filter,
`allowBackup=false`, and `configChanges` `0x40001F80` (including `fontScale`).

Three defects the build caught, all fixed and in the shipped APK:

| # | Fix |
|---|---|
| B1 | `CS0104` — `OperationCanceledException` was ambiguous between `Android.OS` and `System` in `RdpSessionService.cs` (the file has `using Android.OS;`). Now fully qualified. The only hard error in the revision. |
| B2 | `CS8602` — `NotificationManagerCompat.From()` is annotated nullable; `?.Notify` / `?.Cancel` in `RdpSessionService.cs` and `RdpAutoTypeService.cs`. |
| B3 | `data_extraction_rules.xml` used domain `shared_pref`; the correct Android spelling is **`sharedpref`**. aapt accepts the wrong one silently and it only misbehaves at runtime on API 31+. |

**Not published.** The build host has no linked GitHub account (403 on
`alonreich/RDP-Encrypt`), so the release asset must be uploaded from the dev machine:
`build-apk.cmd --publish`, or `gh release upload <tag> compiled\RDPVault.apk --clobber`.

### 5.8 Regressions to never reintroduce

1. A password reveal, copy, share or log anywhere outside the profile editor.
2. `BiometricPrompt` without a `CryptoObject`, or a wrap key without
   `SetUserAuthenticationRequired(true)`.
3. Any string claiming RDP Vault connected, disconnected or ended a session it does not own.
4. Writing vault settings from a control handler during screen population.
5. Hardcoded pixel scroll offsets, fixed control `Height`, or a resolution label naming a
   resolution the user did not choose.
6. Re-arming the Auto-Type prompt on every connect with no way to opt out.
7. Removing `FLAG_SECURE` without an explicit user opt-out.
8. Overwriting `vault.rdpv` with bytes that have not been parsed as a `VaultFile`.
