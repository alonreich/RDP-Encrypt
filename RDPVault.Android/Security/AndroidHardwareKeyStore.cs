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

public sealed class BiometricCryptoResult
{
    public bool Success { get; init; }
    public Cipher? Cipher { get; init; }
    public string? ErrorMessage { get; init; }

    public bool UserCancelled { get; init; }

    public bool KeyPermanentlyInvalidated { get; init; }
}

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
            .SetRandomizedEncryptionRequired(true);

        if (requireBiometrics)
        {
            builder.SetUserAuthenticationRequired(true);

            builder.SetUserAuthenticationValidityDurationSeconds(-1);

            builder.SetInvalidatedByBiometricEnrollment(false);
        }

        keyGenerator.Init(builder.Build());
        keyGenerator.GenerateKey();
    }

    public static string SealMasterKey(string keyId, byte[] masterKey, Cipher cipher)
    {
        byte[] iv = cipher.GetIV() ?? throw new InvalidOperationException("Cipher IV was null.");
        byte[] encrypted = cipher.DoFinal(masterKey) ?? throw new InvalidOperationException("Cipher encryption produced null.");

        return $"{Convert.ToBase64String(iv)}:{Convert.ToBase64String(encrypted)}";
    }

    public static byte[] UnsealMasterKey(string tpmBlob, Cipher cipher)
    {
        string[] parts = tpmBlob.Split(':');
        if (parts.Length != 2) throw new FormatException("Invalid seal format; expected nonce:ciphertext.");

        byte[] ciphertext = Convert.FromBase64String(parts[1]);
        return cipher.DoFinal(ciphertext) ?? throw new CryptographicException("Failed to unseal master key.");
    }

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
                typeName.Contains("UnrecoverableKey", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("KeyPermanentlyInvalidated", StringComparison.OrdinalIgnoreCase) ||
                javaClass.Contains("UnrecoverableKey", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("KeyPermanentlyInvalidated", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("Key permanently invalidated", StringComparison.OrdinalIgnoreCase) ||
                str.Contains("UnrecoverableKey", StringComparison.OrdinalIgnoreCase) ||
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
            base.OnAuthenticationFailed();
        }
    }
}
