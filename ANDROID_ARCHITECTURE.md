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

### 2.3 Embedded FreeRDP Engine
- **Engine**: Bundles `libfreerdp.so` and `libwinpr.so` natively.
- **Benefits**: No dependency on third-party remote desktop applications; enables direct in-memory credential injection, session zeroing via `VaultMemoryGuard`, and scoped trace erasure.
- **Input Engine**: Touch gesture translation:
  - Single tap = Left click
  - Two-finger tap = Right click
  - Drag = Mouse pointer move
  - Pinch-to-zoom / Pan = Framebuffer scaling
  - Virtual overlay keyboard with Windows shortcuts (Ctrl+Alt+Del, Win, Esc, Tab).

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
│   └── Rdp\FreeRdpClient.cs
├── build.cmd                     (Builds & publishes Windows RDPVault.exe)
└── build-apk.cmd                 (Builds & publishes Android RDPVault.apk)
```

### Local Build Requirements
To compile the APK locally:
1. Run `dotnet workload install android`
2. Run `.\build-apk.cmd`
