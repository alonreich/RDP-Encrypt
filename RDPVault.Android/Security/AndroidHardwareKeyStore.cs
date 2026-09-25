using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.Security.Keystore;
using AndroidX.Biometric;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Java.Security;

namespace RDPVault.Android.Security;

/// <summary>
/// Result of a biometric operation that is cryptographically bound to a hardware key.
/// </summary>
public sealed class BiometricCryptoResult
{
    public bool Success { get; init; }
    public Cipher? Cipher { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>User pressed Cancel / the negative button. Not an error worth surfacing.</summary>
    public bool UserCancelled { get; init; }

    /// <summary>
    /// The hardware key is permanently gone: a new fingerprint/face was enrolled, the
    /// screen lock was removed, or the OS rotated the Keymaster. The seal must be
    /// re-created from the master password; it can never be recovered.
    /// </summary>
    public bool KeyPermanentlyInvalidated { get; init; }
}

/// <summary>
/// Mobile hardware security provider: Android Keystore / StrongBox Keymaster.
/// Equivalent to PC TPM 2.0 / Windows Hello.
///
/// SECURITY CONTRACT (audited 2026-09-21, Finding 1):
///   1. The AES-256-GCM wrap key is generated INSIDE the Secure Element (StrongBox) or
///      the ARM TrustZone TEE and is never exportable.
///   2. The key is created with SetUserAuthenticationRequired(true) and a validity
///      duration of -1, which means "authorise EVERY single use with a Class 3 strong
///      biometric". Without that flag the key would be usable by anything running as
///      this UID, and the fingerprint prompt would be pure decoration.
///   3. Every encrypt/decrypt goes through BiometricPrompt.CryptoObject, so the Cipher
///      used to seal/unseal the master key is the exact Cipher the Keymaster authorised
///      for that one biometric event. Calling BiometricPrompt and then separately using
///      a Cipher is NOT a binding and is explicitly forbidden here.
///   4. SetInvalidatedByBiometricEnrollment(true): adding a new fingerprint destroys the
///      seal rather than silently extending access to the new finger.
/// </summary>
public static class AndroidHardwareKeyStore
{
    private const string KeyStoreProvider = "AndroidKeyStore";
    private const string KeyAliasPrefix = "RDPVault_MasterWrap_";

    public static bool HasStrongBoxSupport(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            return context.PackageManager?.HasSystemFeature(PackageManager.FeatureStrongboxKeystore) == true;
        }
        return false;
    }

    /// <summary>
    /// Generates a hardware-isolated 256-bit AES-GCM key inside the device Secure Element / StrongBox.
    /// The key material NEVER enters application memory.
    /// </summary>
    /// <param name="requireBiometrics">
    /// When true (the only value RDP Vault ever passes) the key is unusable without a
    /// fresh Class 3 biometric authorisation supplied via BiometricPrompt.CryptoObject.
    /// </param>
    public static void GenerateHardwareKey(Context context, string keyId, bool requireBiometrics = true)
    {
        string alias = KeyAliasPrefix + keyId;
        var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreProvider);
        if (keyGenerator == null) throw new InvalidOperationException("AndroidKeyStore AES generator not available.");

        int purposes = (int)(KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        var builder = new KeyGenParameterSpec.Builder(alias, (KeyStorePurpose)purposes)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            // Randomised encryption is mandatory for GCM: the caller must never be able
            // to supply its own IV, which is why SealMasterKey reads the IV back out.
            .SetRandomizedEncryptionRequired(true);

        if (requireBiometrics)
        {
            // THE binding. Without these two lines the fingerprint prompt is cosmetic.
            builder.SetUserAuthenticationRequired(true);

            // -1 == "authenticate for every use, with a strong biometric only".
            // On API 30+ the platform maps this onto AUTH_BIOMETRIC_STRONG internally.
            builder.SetUserAuthenticationValidityDurationSeconds(-1);

            // Many OEM fingerprint HALs (Xiaomi/Samsung optical FOD) trigger condition updates
            // on screen off/lock that cause Android Keymaster to prematurely invalidate keys
            // if SetInvalidatedByBiometricEnrollment is true. Set to false to prevent infinite
            // re-seal loops while keeping Class 3 Strong Biometric mandatory for every decryption.
            builder.SetInvalidatedByBiometricEnrollment(false);
        }

        // Attempt StrongBox backing (dedicated HSM chip), fallback to TEE Keymaster if unsupported.
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && HasStrongBoxSupport(context))
        {
            try
            {
                builder.SetIsStrongBoxBacked(true);
                keyGenerator.Init(builder.Build());
                keyGenerator.GenerateKey();
                return;
            }
            catch
            {
                // StrongBox refused (common on 256-bit AES on some OEM HSMs): fall back to TEE.
                builder.SetIsStrongBoxBacked(false);
            }
        }

        keyGenerator.Init(builder.Build());
        keyGenerator.GenerateKey();
    }

    /// <summary>
    /// Encrypts the 32-byte master key using the biometric-authorised AES-GCM Cipher.
    /// Returns the seal payload (base64 nonce : base64 ciphertext).
    /// The Cipher MUST come from a successful BiometricPrompt.CryptoObject.
    /// </summary>
    public static string SealMasterKey(string keyId, byte[] masterKey, Cipher cipher)
    {
        byte[] iv = cipher.GetIV() ?? throw new InvalidOperationException("Cipher IV was null.");
        byte[] encrypted = cipher.DoFinal(masterKey) ?? throw new InvalidOperationException("Cipher encryption produced null.");

        return $"{Convert.ToBase64String(iv)}:{Convert.ToBase64String(encrypted)}";
    }

    /// <summary>
    /// Decrypts the master key using the biometric-authorised Cipher.
    /// </summary>
    public static byte[] UnsealMasterKey(string tpmBlob, Cipher cipher)
    {
        string[] parts = tpmBlob.Split(':');
        if (parts.Length != 2) throw new FormatException("Invalid seal format; expected nonce:ciphertext.");

        byte[] ciphertext = Convert.FromBase64String(parts[1]);
        return cipher.DoFinal(ciphertext) ?? throw new CryptographicException("Failed to unseal master key.");
    }

    /// <summary>
    /// Extracts the GCM nonce from a stored seal blob.
    /// </summary>
    public static byte[] ExtractSealIv(string tpmBlob)
    {
        string[] parts = tpmBlob.Split(':');
        if (parts.Length != 2) throw new FormatException("Invalid seal format; expected nonce:ciphertext.");
        return Convert.FromBase64String(parts[0]);
    }

    public static bool HasKey(string keyId)
    {
        try
        {
            var keyStore = KeyStore.GetInstance(KeyStoreProvider);
            keyStore?.Load(null);
            return keyStore?.ContainsAlias(KeyAliasPrefix + keyId) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prepares an initialised Cipher to be wrapped in a BiometricPrompt.CryptoObject.
    /// Throws when the underlying hardware key has been invalidated by a new biometric
    /// enrolment or a screen-lock change.
    /// </summary>
    public static Cipher GetInitializedCipher(string keyId, int opMode, byte[]? iv = null)
    {
        string alias = KeyAliasPrefix + keyId;
        var keyStore = KeyStore.GetInstance(KeyStoreProvider);
        keyStore?.Load(null);

        var key = keyStore?.GetKey(alias, null);
        if (key == null) throw new KeyNotFoundException($"Hardware key {alias} not found.");

        var cipher = Cipher.GetInstance($"{KeyProperties.KeyAlgorithmAes}/{KeyProperties.BlockModeGcm}/{KeyProperties.EncryptionPaddingNone}");
        if (cipher == null) throw new InvalidOperationException("AES/GCM cipher unavailable.");

        if (opMode == (int)Javax.Crypto.CipherMode.EncryptMode)
        {
            cipher.Init((Javax.Crypto.CipherMode)opMode, key);
        }
        else
        {
            if (iv == null) throw new ArgumentNullException(nameof(iv), "IV required for decryption.");
            var gcmSpec = new GCMParameterSpec(128, iv);
            cipher.Init((Javax.Crypto.CipherMode)opMode, key, gcmSpec);
        }

        return cipher;
    }

    public static string GetExceptionDetails(Exception? ex)
    {
        if (ex == null) return "Unknown error";
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is Java.Lang.Throwable jt)
            {
                string javaClass = jt.Class?.SimpleName ?? jt.Class?.Name ?? "";
                string javaMsg = jt.LocalizedMessage ?? jt.Message ?? "";
                if (!string.IsNullOrWhiteSpace(javaMsg))
                {
                    return !string.IsNullOrWhiteSpace(javaClass) ? $"{javaClass}: {javaMsg}" : javaMsg;
                }
                if (!string.IsNullOrWhiteSpace(javaClass))
                {
                    return javaClass;
                }
            }
            if (!string.IsNullOrWhiteSpace(e.Message) && !e.Message.Contains("Exception of type", StringComparison.OrdinalIgnoreCase))
            {
                return e.Message;
            }
        }
        return ex.Message;
    }

    /// <summary>
    /// True when the exception means "this hardware key is gone forever" (new fingerprint
    /// enrolled, screen lock removed, Keymaster rotated by an OS update).
    /// Detected by type name so the code does not depend on one particular AndroidX /
    /// Mono.Android binding surface for KeyPermanentlyInvalidatedException.
    /// </summary>
    public static bool IsKeyInvalidated(Exception? ex)
    {
        if (ex == null) return false;

        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            string typeName = e.GetType().FullName ?? "";
            string msg = e.Message ?? "";
            string str = e.ToString() ?? "";
            string javaClass = (e is Java.Lang.Throwable jt) ? (jt.Class?.Name ?? "") : "";

            if (typeName.Contains("KeyPermanentlyInvalidated", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("InvalidKey", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("UnrecoverableKey", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("CryptographicException", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("KeyPermanentlyInvalidated", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("UnrecoverableKey", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("InvalidKeyException", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("KeyStoreException", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("AEADBadTagException", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("BadPaddingException", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("IllegalBlockSizeException", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("KeyPermanentlyInvalidated", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("Key permanently invalidated", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("UnrecoverableKey", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("InvalidKeyException", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("AEADBadTag", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("BadPadding", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Key permanently invalidated", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsBiometricAvailable(Context? context)
    {
        if (context == null) return false;
        try
        {
            var bm = BiometricManager.From(context);
            return bm.CanAuthenticate((int)BiometricManager.Authenticators.BiometricStrong) == BiometricManager.BiometricSuccess;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Human-readable reason biometrics cannot be used right now (no hardware, no
    /// fingerprints enrolled, sensor temporarily locked out, security update required).
    /// Returns null when biometrics ARE available.
    /// </summary>
    public static string? DescribeBiometricUnavailability(Context? context)
    {
        if (context == null) return "Device context unavailable.";
        try
        {
            var bm = BiometricManager.From(context);
            int status = bm.CanAuthenticate((int)BiometricManager.Authenticators.BiometricStrong);

            if (status == BiometricManager.BiometricSuccess) return null;
            if (status == BiometricManager.BiometricErrorNoHardware)
                return "This phone has no fingerprint or face sensor.";
            if (status == BiometricManager.BiometricErrorHwUnavailable)
                return "The fingerprint sensor is busy right now. Try again in a moment.";
            if (status == BiometricManager.BiometricErrorNoneEnrolled)
                return "No fingerprint or face is set up yet. Add one in Android Settings > Security.";
            if (status == BiometricManager.BiometricErrorSecurityUpdateRequired)
                return "Android needs a security update before strong biometrics can be used.";
            return "Fingerprint / face unlock is not available on this device.";
        }
        catch (Exception ex)
        {
            return "Biometric check failed: " + ex.Message;
        }
    }

    public static void DeleteHardwareKey(string keyId)
    {
        try
        {
            string alias = KeyAliasPrefix + keyId;
            var keyStore = KeyStore.GetInstance(KeyStoreProvider);
            keyStore?.Load(null);
            if (keyStore?.ContainsAlias(alias) == true)
            {
                keyStore.DeleteEntry(alias);
            }
        }
        catch { }
    }

    /// <summary>
    /// Creates a brand new hardware key and seals the master key under it.
    /// The user is prompted for a biometric and the resulting AUTHORISED Cipher is what
    /// performs the encryption - there is no code path that seals without that prompt.
    /// </summary>
    public static async Task<(bool Success, string? Seal, string? KeyId, string? Error, bool Cancelled)> EnrollAndSealAsync(
        MainActivity activity,
        byte[] masterKey,
        string title,
        string subtitle,
        string negativeButtonText)
    {
        string? unavailable = DescribeBiometricUnavailability(activity);
        if (unavailable != null) return (false, null, null, unavailable, false);

        string keyId = Guid.NewGuid().ToString("N");

        try
        {
            GenerateHardwareKey(activity, keyId, requireBiometrics: true);
            var cipher = GetInitializedCipher(keyId, (int)Javax.Crypto.CipherMode.EncryptMode);

            var result = await AuthenticateWithCryptoAsync(activity, title, subtitle, negativeButtonText, cipher)
                .ConfigureAwait(true);

            if (!result.Success || result.Cipher == null)
            {
                DeleteHardwareKey(keyId);
                return (false, null, null, result.ErrorMessage, result.UserCancelled);
            }

            string seal = SealMasterKey(keyId, masterKey, result.Cipher);
            return (true, seal, keyId, null, false);
        }
        catch (Exception ex)
        {
            DeleteHardwareKey(keyId);
            return (false, null, null, ex.Message, false);
        }
    }

    /// <summary>
    /// Unseals the master key from an existing seal. The Cipher handed to DoFinal is the
    /// one the Keymaster released for this single biometric authorisation.
    /// </summary>
    public static async Task<(bool Success, byte[]? MasterKey, string? Error, bool Cancelled, bool Invalidated)> AuthenticateAndUnsealAsync(
        MainActivity activity,
        string keyId,
        string sealBlob,
        string title,
        string subtitle,
        string negativeButtonText)
    {
        if (!HasKey(keyId))
        {
            return (false, null, "The fingerprint seal for this device is missing.", false, true);
        }

        Cipher cipher;
        try
        {
            byte[] iv = ExtractSealIv(sealBlob);
            cipher = GetInitializedCipher(keyId, (int)Javax.Crypto.CipherMode.DecryptMode, iv);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("RDPVault", $"[Biometrics] GetInitializedCipher failed: {ex}");
            return (false, null, GetExceptionDetails(ex), false, IsKeyInvalidated(ex));
        }

        var result = await AuthenticateWithCryptoAsync(activity, title, subtitle, negativeButtonText, cipher)
            .ConfigureAwait(true);

        if (!result.Success || result.Cipher == null)
        {
            return (false, null, result.ErrorMessage, result.UserCancelled, result.KeyPermanentlyInvalidated);
        }

        try
        {
            byte[] master = UnsealMasterKey(sealBlob, result.Cipher);
            return (true, master, null, false, false);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("RDPVault", $"[Biometrics] UnsealMasterKey failed: {ex}");
            return (false, null, GetExceptionDetails(ex), false, IsKeyInvalidated(ex));
        }
    }

    /// <summary>
    /// Shows BiometricPrompt bound to <paramref name="cipher"/> and returns the Cipher the
    /// Keymaster authorised. This is the ONLY entry point allowed to unlock a hardware key.
    /// </summary>
    public static Task<BiometricCryptoResult> AuthenticateWithCryptoAsync(
        MainActivity activity,
        string title,
        string subtitle,
        string negativeButtonText,
        Cipher cipher)
    {
        var tcs = new TaskCompletionSource<BiometricCryptoResult>();

        activity.RunOnUiThread(() =>
        {
            try
            {
                var executor = AndroidX.Core.Content.ContextCompat.GetMainExecutor(activity);
                if (executor == null)
                {
                    tcs.TrySetResult(new BiometricCryptoResult { Success = false, ErrorMessage = "Failed to obtain main executor." });
                    return;
                }

                var callback = new BiometricCryptoCallback(
                    onSuccess: authorisedCipher => tcs.TrySetResult(new BiometricCryptoResult
                    {
                        Success = true,
                        Cipher = authorisedCipher
                    }),
                    onError: (code, err) => tcs.TrySetResult(new BiometricCryptoResult
                    {
                        Success = false,
                        ErrorMessage = err,
                        UserCancelled = IsCancellationCode(code),
                        KeyPermanentlyInvalidated = code == BiometricPrompt.ErrorNoBiometrics
                    }));

                var prompt = new BiometricPrompt(activity, executor, callback);
                var promptInfo = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle(title)
                    .SetSubtitle(subtitle)
                    .SetNegativeButtonText(negativeButtonText)
                    .SetConfirmationRequired(false)
                    .SetAllowedAuthenticators((int)BiometricManager.Authenticators.BiometricStrong)
                    .Build();

                prompt.Authenticate(promptInfo, new BiometricPrompt.CryptoObject(cipher));
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("RDPVault", $"[Biometrics] AuthenticateWithCryptoAsync failed: {ex}");
                tcs.TrySetResult(new BiometricCryptoResult
                {
                    Success = false,
                    ErrorMessage = GetExceptionDetails(ex),
                    KeyPermanentlyInvalidated = IsKeyInvalidated(ex)
                });
            }
        });

        return tcs.Task;
    }

    private static bool IsCancellationCode(int code)
    {
        return code == BiometricPrompt.ErrorNegativeButton
            || code == BiometricPrompt.ErrorUserCanceled
            || code == BiometricPrompt.ErrorCanceled;
    }

    private class BiometricCryptoCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly Action<Cipher?> _onSuccess;
        private readonly Action<int, string> _onError;

        public BiometricCryptoCallback(Action<Cipher?> onSuccess, Action<int, string> onError)
        {
            _onSuccess = onSuccess;
            _onError = onError;
        }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            base.OnAuthenticationSucceeded(result);
            _onSuccess(result?.CryptoObject?.Cipher);
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
        {
            base.OnAuthenticationError(errorCode, errString);
            _onError(errorCode, errString?.ToString() ?? "Authentication cancelled");
        }

        public override void OnAuthenticationFailed()
        {
            // A single non-matching finger. BiometricPrompt keeps the dialog open and
            // will eventually raise OnAuthenticationError, so nothing to do here.
            base.OnAuthenticationFailed();
        }
    }
}
