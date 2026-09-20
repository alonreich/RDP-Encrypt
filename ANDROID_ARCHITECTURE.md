# RDP Vault Android Companion Architecture

## 1. Executive Summary
RDP Vault Android is a parallel distribution branch providing zero-compromise encrypted remote desktop capabilities on mobile devices. It shares the identical cryptographic core, in-memory credential protection, and JSON vault structure (`vault.rdpv`) as the Windows desktop version.

---

## 2. Key Architecture Pillars

### 2.1 Hardware Security Root of Trust (Mobile TPM Equivalent)
- **Technology**: Android Keystore with StrongBox Keymaster.
- **Hardware Layer**: Runs inside the device's dedicated Hardware Security Module (HSM / Secure Element) on supported devices (Pixel, Samsung Knox), falling back gracefully to the ARM TrustZone Trusted Execution Environment (TEE).
- **Authentication**: Requires Class 3 Strong Biometrics (Fingerprint or 3D Face Unlock) via `BiometricPrompt.CryptoObject`.
- **Compromise Resistance**: Even if the Android OS is rooted, dumped, or compromised, hardware-bound master keys cannot be exported from the secure element.

### 2.2 Screen Rotation & Session Continuity (Zero Mid-Session Re-Authentication)
- **Problem Solved**: Standard Android apps recreate the `Activity` on orientation flip, destroying in-memory keys and prompting for biometrics mid-session.
- **Solution**:
  1. `AndroidManifest.xml` declares:
     `android:configChanges="orientation|screenSize|screenLayout|smallestScreenSize|uiMode|density"`
     Orientation changes are handled in-place via `OnConfigurationChanged` without destroying the activity.
  2. The active FreeRDP socket connection, framebuffer, and memory-protected credentials reside inside a dedicated Android `ForegroundService` (`RdpSessionService`).
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
7. **Active Remote Session Banner Redesign**: Vertical card layout with equal-width Resume/End action buttons and a dedicated "✕ Dismiss" control.
8. **In-Place Vault Restore Confirmation**: `OverlayConfirmRestoreVault` safeguards against accidental vault overwrites during backup imports, and files standardize on `.rdpv`.
9. **Collapsible Wake-on-LAN**: `PnlWolDetails` expands only when WOL is enabled, and enforces MAC address normalization via `MacAddressHelper.TryNormalizeMac`.
10. **Configurable Auto-Lock**: `CmbSettingsAutoLock` dropdown provides 1, 5, 15, 30, 60 minutes, or Never timeouts.

### 4.2 Architectural Findings Resolved
- **Finding A (Native FreeRDP Cleanup)**: Obsolete native FreeRDP P/Invoke stubs and dead `PanelSession` removed.
- **Finding B (Sensitive Clipboard & Resume Auto-Wipe)**: Copied passwords and recovery keys are flagged with `android.content.extra.IS_SENSITIVE` on Android 13+ and automatically purged via `CheckAndWipeExpiredClipboard()` on app resume.
- **Finding C (Foreground Service & Android 14+ Permissions)**: Service declared as `connectedDevice` with `POST_NOTIFICATIONS` runtime permission request.
- **Finding D (Intent Handoff Parameter Integrity)**: Parameterized `rdp://` URI handoff with fallback to Google Play Store.
- **Finding E (Search Debounce & Status Feedback)**: 150ms debounce on search queries with distinct feedback for empty vault vs no matching query.

