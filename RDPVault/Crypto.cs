using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Isopoh.Cryptography.Argon2;

namespace RDPVault;

public static class VaultCrypto
{
    private const int MasterKeyBytes = 32;
    private const int NonceBytes = 12;

    public static byte[] DerivePasswordKey(string password, VaultFile.KdfParams kdf)
    {
        byte[] salt = Convert.FromBase64String(kdf.Salt);
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            MemoryCost = kdf.Mem,
            TimeCost = kdf.Iter,
            Lanes = kdf.Lanes,
            Threads = kdf.Lanes,
            Password = Encoding.UTF8.GetBytes(password),
            Salt = salt,
            HashLength = MasterKeyBytes
        };
        var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        byte[] key = new byte[MasterKeyBytes];
        Array.Copy(hash.Buffer, key, MasterKeyBytes);
        return key;
    }

    private static (byte[] nonce, byte[] ct) AeadSeal(byte[] key, byte[] plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] ct = new byte[plaintext.Length + 16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length));
        return (nonce, ct);
    }

    private static byte[] AeadOpen(byte[] key, byte[] nonce, byte[] ct)
    {
        byte[] pt = new byte[ct.Length - 16];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, ct.AsSpan(0, ct.Length - 16), ct.AsSpan(ct.Length - 16), pt);
        return pt;
    }

    private static VaultFile.WrappedBlob SealToBlob(byte[] key, byte[] data)
    {
        var (nonce, ct) = AeadSeal(key, data);
        return new VaultFile.WrappedBlob { Nonce = Convert.ToBase64String(nonce), Ct = Convert.ToBase64String(ct) };
    }

    private static byte[] OpenBlob(byte[] key, VaultFile.WrappedBlob blob)
        => AeadOpen(key, Convert.FromBase64String(blob.Nonce), Convert.FromBase64String(blob.Ct));

    public static VaultFile CreateVault(string password, VaultPayload payload, string vaultPath)
    {
        var file = new VaultFile();
        file.Kdf.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        byte[] passwordKey = DerivePasswordKey(password, file.Kdf);
        try
        {
            byte[] master = RandomNumberGenerator.GetBytes(MasterKeyBytes);
            file.Wrap = SealToBlob(passwordKey, master);
            file.Data = SealToBlob(master, Serialize(payload));
            File.WriteAllText(vaultPath, JsonSerializer.Serialize(file, VaultJsonContext.Default.VaultFile));
            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
        }
    }

    public static (byte[] Master, VaultPayload Payload) Open(VaultFile file, string password)
    {
        byte[] passwordKey = DerivePasswordKey(password, file.Kdf);
        try
        {
            byte[] master;
            try { master = OpenBlob(passwordKey, file.Wrap); }
            catch (CryptographicException) { throw new InvalidDataException("Wrong password."); }

            VaultPayload payload;
            try { payload = Deserialize(OpenBlob(master, file.Data)); }
            catch (CryptographicException) { throw new InvalidDataException("Vault data is corrupted."); }
            return (master, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
        }
    }

    public static void Save(VaultFile file, byte[] master, VaultPayload payload, string vaultPath,
                            string? newPassword = null, List<SealEntry>? newSeals = null)
    {
        if (newSeals != null) file.Seals = newSeals;

        if (newPassword != null)
        {
            file.Kdf.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            file.Kdf.Mem = 262144;
            file.Kdf.Iter = 5;
            byte[] passwordKey = DerivePasswordKey(newPassword, file.Kdf);
            try { file.Wrap = SealToBlob(passwordKey, master); }
            finally { CryptographicOperations.ZeroMemory(passwordKey); }
        }

        file.Data = SealToBlob(master, Serialize(payload));

        string tmp = vaultPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, VaultJsonContext.Default.VaultFile));
        if (File.Exists(vaultPath)) File.Replace(tmp, vaultPath, null);
        else File.Move(tmp, vaultPath);
    }

    public static VaultPayload OpenPayload(VaultFile file, byte[] master)
        => Deserialize(OpenBlob(master, file.Data));

    public static string CurrentMachineId()
    {
        string machine = Environment.MachineName;
        string domain = Environment.UserDomainName;
        string user = Environment.UserName;
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes($"{machine}|{domain}|{user}"));
        return Convert.ToHexString(h, 0, 8);
    }

    public static byte[] DeriveTpmKey(byte[] signature, string saltBase64)
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            MemoryCost = 65536,
            TimeCost = 2,
            Lanes = 4,
            Threads = 4,
            Password = signature,
            Salt = Convert.FromBase64String(saltBase64),
            HashLength = MasterKeyBytes
        };
        var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        byte[] key = new byte[MasterKeyBytes];
        Array.Copy(hash.Buffer, key, MasterKeyBytes);
        return key;
    }

    public static SealEntry SealTpm(byte[] master, string keyId, byte[] signature, VaultFile file)
    {
        byte[] tpmKey = DeriveTpmKey(signature, file.Kdf.Salt);
        var blob = SealToBlob(tpmKey, master);
        CryptographicOperations.ZeroMemory(tpmKey);

        return new SealEntry
        {
            MachineId = CurrentMachineId(),
            KeyId = keyId,
            TpmBlob = $"{blob.Nonce}:{blob.Ct}"
        };
    }

    public static byte[]? UnsealTpm(VaultFile file, SealEntry seal, byte[] signature)
    {
        byte[] tpmKey = DeriveTpmKey(signature, file.Kdf.Salt);
        try
        {
            string[] parts = seal.TpmBlob.Split(':');
            if (parts.Length != 2) return null;
            var blob = new VaultFile.WrappedBlob { Nonce = parts[0], Ct = parts[1] };
            return OpenBlob(tpmKey, blob);
        }
        catch
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tpmKey);
        }
    }

    private static byte[] Serialize(VaultPayload p) => JsonSerializer.SerializeToUtf8Bytes(p, VaultJsonContext.Default.VaultPayload);

    private static VaultPayload Deserialize(byte[] json) =>
        JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultPayload) ?? new VaultPayload();
}