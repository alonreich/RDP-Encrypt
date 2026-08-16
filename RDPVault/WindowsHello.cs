using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.Credentials;
using Windows.Storage.Streams;

namespace RDPVault;

public enum HelloEnrollResult
{
    Success,
    Cancelled,
    NotSupported,
    /// <summary>Issue #18c: this platform's key does not sign deterministically.</summary>
    SignatureNotReproducible
}

public static class WindowsHello
{
    private const string KeyName = "RDPVaultQuickUnlock";

    private static byte[] Challenge()
        => Encoding.UTF8.GetBytes("RDPVault::QuickUnlock::v2::" + VaultCrypto.CurrentMachineId());

    public static Task<bool> IsSupportedAsync() => KeyCredentialManager.IsSupportedAsync().AsTask();

    /// <summary>
    /// ISSUE #18c.
    /// Quick unlock derives the vault key from the raw TPM signature bytes. That only
    /// works if signing the same challenge twice yields identical bytes - true for
    /// RSA PKCS#1 v1.5, false for randomised schemes such as RSA-PSS or ECDSA.
    ///
    /// Previously the app enrolled blindly. On a platform that randomises signatures
    /// enrollment appeared to succeed and then quick unlock failed forever, with the
    /// user told only "Hardware signature rejected".
    ///
    /// Enrollment now signs twice and compares. If the bytes differ we refuse to
    /// enroll and say so plainly, and we always test-unseal the freshly written seal
    /// before it is trusted (see SessionManager.EnableHelloSealAsync).
    /// </summary>
    public static async Task<(HelloEnrollResult Result, string KeyId, byte[] Signature)> EnrollAndSignAsync()
    {
        // Issue #21: keep the OS credential prompt in the foreground for the whole
        // enrollment, including both signatures.
        using var focus = SystemPromptFocus.Begin();
        try
        {
            if (!await IsSupportedAsync())
                return (HelloEnrollResult.NotSupported, "", Array.Empty<byte>());

            var created = await KeyCredentialManager.RequestCreateAsync(
                KeyName, KeyCredentialCreationOption.ReplaceExisting);
            if (created.Status != KeyCredentialStatus.Success)
                return (HelloEnrollResult.Cancelled, "", Array.Empty<byte>());

            string keyId = PublicKeyFingerprint(created.Credential);

            byte[]? first = await SignAsync(created.Credential);
            if (first == null) return (HelloEnrollResult.Cancelled, "", Array.Empty<byte>());

            byte[]? second = await SignAsync(created.Credential);
            if (second == null) return (HelloEnrollResult.Cancelled, "", Array.Empty<byte>());

            if (!CryptographicOperations.FixedTimeEquals(first, second))
                return (HelloEnrollResult.SignatureNotReproducible, "", Array.Empty<byte>());

            return (HelloEnrollResult.Success, keyId, first);
        }
        catch
        {
            return (HelloEnrollResult.NotSupported, "", Array.Empty<byte>());
        }
    }

    public static async Task<byte[]?> GetSignatureAsync(string expectedKeyId)
    {
        // Issue #21: the fingerprint / face prompt must own the foreground the
        // moment it appears, otherwise the sensor reading is discarded and the user
        // has to click the dialog first.
        using var focus = SystemPromptFocus.Begin();
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(KeyName);
            if (opened.Status != KeyCredentialStatus.Success) return null;

            // The public key must be the exact one this vault was sealed against.
            if (PublicKeyFingerprint(opened.Credential) != expectedKeyId) return null;

            return await SignAsync(opened.Credential);
        }
        catch { return null; }
    }

    private static async Task<byte[]?> SignAsync(KeyCredential credential)
    {
        var result = await credential.RequestSignAsync(
            CryptographicBuffer.CreateFromByteArray(Challenge()));
        if (result.Status != KeyCredentialStatus.Success) return null;
        CryptographicBuffer.CopyToByteArray(result.Result, out byte[] signature);
        return signature;
    }

    // RetrievePublicKey is SYNCHRONOUS in the .NET projection - there is no Async overload.
    private static string PublicKeyFingerprint(KeyCredential credential)
    {
        IBuffer key = credential.RetrievePublicKey(CryptographicPublicKeyBlobType.BCryptPublicKey);
        CryptographicBuffer.CopyToByteArray(key, out byte[] raw);
        return Convert.ToHexString(SHA256.HashData(raw));
    }
}
