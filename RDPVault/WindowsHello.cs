using System.Security.Cryptography;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.Credentials;
using Windows.Storage.Streams;

namespace RDPVault;

/// <summary>
/// Windows Hello quick unlock.
/// The Hello key ("RDPVaultQuickUnlock") lives in this PC's TPM / Microsoft Passport
/// store and can only sign after the user proves identity with fingerprint / face / PIN.
/// We verify the key is THE key enrolled at setup (public-key hash match) and that
/// Windows confirmed the user (successful SignAsync). Only then is the DPAPI seal used.
/// </summary>
public static class WindowsHello
{
    private const string KeyName = "RDPVaultQuickUnlock";

    /// <summary>The message signed at every unlock. Bound to this machine's identity.</summary>
    private static byte[] Challenge()
        => Encoding.UTF8.GetBytes("RDPVault::QuickUnlock::v1::" + VaultCrypto.CurrentMachineId());

    public static Task<bool> IsSupportedAsync() => KeyCredentialManager.IsSupportedAsync().AsTask();

    /// <summary>
    /// Enroll this PC (prompts Hello once). Returns the public-key fingerprint
    /// (hex SHA-256) to store in the vault's seal, or null if unsupported/cancelled.
    /// </summary>
    public static async Task<string?> EnrollAsync()
    {
        try
        {
            var created = await KeyCredentialManager.RequestCreateAsync(
                KeyName, KeyCredentialCreationOption.ReplaceExisting);
            if (created.Status != KeyCredentialStatus.Success) return null;
            return await PublicKeyFingerprintAsync(created.Credential);
        }
        catch { return null; }
    }

    /// <summary>
    /// Prompt Hello now. True only if: key opens, its fingerprint matches the one
    /// stored in the vault, and Windows reports a successful user-verified signature.
    /// </summary>
    public static async Task<bool> VerifyAsync(string expectedKeyId)
    {
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(KeyName);
            if (opened.Status != KeyCredentialStatus.Success) return false;

            // Key must be the very one enrolled when quick unlock was enabled.
            if (await PublicKeyFingerprintAsync(opened.Credential) != expectedKeyId) return false;

            // RequestSignAsync triggers the actual fingerprint / face / PIN prompt.
            var result = await opened.Credential.RequestSignAsync(
                CryptographicBuffer.CreateFromByteArray(Challenge()));
            return result.Status == KeyCredentialStatus.Success;
        }
        catch { return false; }
    }

    private static Task<string> PublicKeyFingerprintAsync(KeyCredential credential)
    {
        IBuffer key = credential.RetrievePublicKey(
            CryptographicPublicKeyBlobType.BCryptPublicKey);
        CryptographicBuffer.CopyToByteArray(key, out byte[] raw);
        return Task.FromResult(Convert.ToHexString(SHA256.HashData(raw)));
    }
}