
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

### 5.7 Build status

This revision was authored without the .NET Android workload available, so **it has not been
compiled**. Run `build-apk.cmd` and resolve any AndroidX binding-name mismatches before
release — check `BiometricPrompt.ErrorNegativeButton` / `ErrorUserCanceled` /
`ErrorCanceled` and `BiometricManager.BiometricErrorSecurityUpdateRequired` first.

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
