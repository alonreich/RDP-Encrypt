using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.Credentials;
using Windows.Storage.Streams;

namespace RDPVault;

public static class WindowsHello
{
    private const string KeyName = "RDPVaultQuickUnlock";

    private static byte[] Challenge()
        => Encoding.UTF8.GetBytes("RDPVault::QuickUnlock::v2::" + VaultCrypto.CurrentMachineId());

    public static Task<bool> IsSupportedAsync() => KeyCredentialManager.IsSupportedAsync().AsTask();

    public static async Task<(string KeyId, byte[] Signature)?> EnrollAndSignAsync()
    {
        try
        {
            var created = await KeyCredentialManager.RequestCreateAsync(
                KeyName, KeyCredentialCreationOption.ReplaceExisting);
            if (created.Status != KeyCredentialStatus.Success) return null;

            string keyId = await PublicKeyFingerprintAsync(created.Credential);

            var result = await created.Credential.RequestSignAsync(
                CryptographicBuffer.CreateFromByteArray(Challenge()));
            if (result.Status != KeyCredentialStatus.Success) return null;

            CryptographicBuffer.CopyToByteArray(result.Result, out byte[] signature);
            return (keyId, signature);
        }
        catch { return null; }
    }

    public static async Task<byte[]?> GetSignatureAsync(string expectedKeyId)
    {
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(KeyName);
            if (opened.Status != KeyCredentialStatus.Success) return null;

            if (await PublicKeyFingerprintAsync(opened.Credential) != expectedKeyId) return null;

            var result = await opened.Credential.RequestSignAsync(
                CryptographicBuffer.CreateFromByteArray(Challenge()));
            if (result.Status != KeyCredentialStatus.Success) return null;

            CryptographicBuffer.CopyToByteArray(result.Result, out byte[] signature);
            return signature;
        }
        catch { return null; }
    }

    private static Task<string> PublicKeyFingerprintAsync(KeyCredential credential)
    {
        IBuffer key = credential.RetrievePublicKey(CryptographicPublicKeyBlobType.BCryptPublicKey);
        CryptographicBuffer.CopyToByteArray(key, out byte[] raw);
        return Task.FromResult(Convert.ToHexString(SHA256.HashData(raw)));
    }
}