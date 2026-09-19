using System;
using System.IO;
using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Security.Keystore;
using AndroidX.Biometric;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Java.Security;
using RDPVault;

namespace RDPVault.Android.Security;

/// <summary>
/// Mobile hardware security provider: Android Keystore / StrongBox Keymaster.
/// Equivalent to PC TPM 2.0 / Windows Hello.
/// Keys are generated directly inside the dedicated Hardware Security Module (StrongBox/TEE)
/// and require Class 3 Strong Biometrics (Fingerprint/3D Face) to authorize cryptographic operations.
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
    /// The private key material NEVER enters application memory.
    /// </summary>
    public static void GenerateHardwareKey(Context context, string keyId, bool requireBiometrics = true)
    {
        string alias = KeyAliasPrefix + keyId;
        var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreProvider);
        if (keyGenerator == null) throw new InvalidOperationException("AndroidKeyStore AES generator not available.");

        int purposes = (int)(KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        var builder = new KeyGenParameterSpec.Builder(alias, (KeyStorePurpose)purposes)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256);

        if (requireBiometrics)
        {
            builder.SetUserAuthenticationRequired(true);
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                builder.SetUserAuthenticationParameters(0, (int)KeyPropertiesAuthType.BiometricStrong);
            }
        }

        // Attempt StrongBox backing (dedicated HSM chip), fallback to TEE Keymaster if not supported
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
                // Fall back to standard TEE Keymaster
                builder.SetIsStrongBoxBacked(false);
            }
        }

        keyGenerator.Init(builder.Build());
        keyGenerator.GenerateKey();
    }

    /// <summary>
    /// Encrypts the 32-byte master key using the hardware-bound AES-GCM key.
    /// Returns the TPM-compatible seal payload (nonce:ciphertext).
    /// </summary>
    public static string SealMasterKey(string keyId, byte[] masterKey, Cipher cipher)
    {
        byte[] iv = cipher.GetIV() ?? throw new InvalidOperationException("Cipher IV was null.");
        byte[] encrypted = cipher.DoFinal(masterKey) ?? throw new InvalidOperationException("Cipher encryption produced null.");

        return $"{Convert.ToBase64String(iv)}:{Convert.ToBase64String(encrypted)}";
    }

    /// <summary>
    /// Decrypts the master key using the authorized biometric Cipher.
    /// </summary>
    public static byte[] UnsealMasterKey(string tpmBlob, Cipher cipher)
    {
        string[] parts = tpmBlob.Split(':');
        if (parts.Length != 2) throw new FormatException("Invalid seal format; expected nonce:ciphertext.");

        byte[] ciphertext = Convert.FromBase64String(parts[1]);
        return cipher.DoFinal(ciphertext) ?? throw new CryptographicException("Failed to unseal master key.");
    }

    /// <summary>
    /// Prepares an initialized Cipher instance for wrapping with BiometricPrompt.CryptoObject.
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

    public static Task<(bool Success, Cipher? Cipher, string? ErrorMessage)> AuthenticatePromptAsync(
        MainActivity activity,
        Cipher cipher,
        string title,
        string subtitle,
        string negativeButtonText)
    {
        var tcs = new TaskCompletionSource<(bool Success, Cipher? Cipher, string? ErrorMessage)>();

        activity.RunOnUiThread(() =>
        {
            try
            {
                var executor = AndroidX.Core.Content.ContextCompat.GetMainExecutor(activity);
                if (executor == null)
                {
                    tcs.TrySetResult((false, null, "Failed to obtain main executor."));
                    return;
                }

                var callback = new BiometricAuthCallback(
                    onSuccess: res => tcs.TrySetResult((true, res.CryptoObject?.Cipher, null)),
                    onError: (code, err) => tcs.TrySetResult((false, null, err)));

                var prompt = new BiometricPrompt(activity, executor, callback);
                var promptInfo = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle(title)
                    .SetSubtitle(subtitle)
                    .SetNegativeButtonText(negativeButtonText)
                    .SetAllowedAuthenticators((int)BiometricManager.Authenticators.BiometricStrong)
                    .Build();

                var cryptoObject = new BiometricPrompt.CryptoObject(cipher);
                prompt.Authenticate(promptInfo, cryptoObject);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult((false, null, ex.Message));
            }
        });

        return tcs.Task;
    }

    private class BiometricAuthCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly Action<BiometricPrompt.AuthenticationResult> _onSuccess;
        private readonly Action<int, string> _onError;

        public BiometricAuthCallback(
            Action<BiometricPrompt.AuthenticationResult> onSuccess,
            Action<int, string> onError)
        {
            _onSuccess = onSuccess;
            _onError = onError;
        }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            base.OnAuthenticationSucceeded(result);
            _onSuccess(result);
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
