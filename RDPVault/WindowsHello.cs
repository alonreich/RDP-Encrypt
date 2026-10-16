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
    SignatureNotReproducible
}

public static class WindowsHello
{
    private const string KeyName = "RDPVaultQuickUnlock";

    private static byte[] Challenge()
        => Encoding.UTF8.GetBytes("RDPVault::QuickUnlock::v2::" + VaultCrypto.CurrentMachineId());

    public static Task<bool> IsSupportedAsync() => KeyCredentialManager.IsSupportedAsync().AsTask();

    public static async Task<(HelloEnrollResult Result, string KeyId, byte[] Signature)> EnrollAndSignAsync()
    {
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
        using var focus = SystemPromptFocus.Begin();
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(KeyName);
            if (opened.Status != KeyCredentialStatus.Success) return null;

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

    private static string PublicKeyFingerprint(KeyCredential credential)
    {
        IBuffer key = credential.RetrievePublicKey(CryptographicPublicKeyBlobType.BCryptPublicKey);
        CryptographicBuffer.CopyToByteArray(key, out byte[] raw);
        return Convert.ToHexString(SHA256.HashData(raw));
    }
}
