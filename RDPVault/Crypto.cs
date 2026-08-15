using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;

namespace RDPVault;

/// <summary>
/// Vault cryptography:
/// - Password --Argon2id--> password key --AES-GCM unwrap--> master key --AES-GCM--> payload.
/// - Optional per-PC "seals": master key wrapped by DPAPI (CurrentUser) so the same
///   Windows account can unseal it instantly, optionally gated by Windows Hello.
/// No plaintext ever touches disk; keys live in pinned/locked byte arrays in memory.
/// </summary>
public static class VaultCrypto
{
    private const int MasterKeyBytes = 32; // AES-256
    private const int NonceBytes = 12;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---------- Argon2id ----------

    public static byte[] DerivePasswordKey(string password, VaultFile.KdfParams kdf)
    {
        byte[] salt = Convert.FromBase64String(kdf.Salt);
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing, // Argon2id
            Version = Argon2Version.Nineteen,
            MemoryCost = kdf.Mem,       // KiB
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

    // ---------- AES-GCM helpers ----------

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

    // ---------- Create / open / save ----------

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
            File.WriteAllText(vaultPath, JsonSerializer.Serialize(file, JsonOpts));
            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
        }
    }

    /// <summary>Verify password & return (masterKey, payload). Throws InvalidDataException on wrong password.</summary>
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

    /// <summary>Re-save payload (and optionally new password / new seals) under the given master key.</summary>
    public static void Save(VaultFile file, byte[] master, VaultPayload payload, string vaultPath,
                            string? newPassword = null, List<SealEntry>? newSeals = null)
    {
        if (newSeals != null) file.Seals = newSeals;

        if (newPassword != null)
        {
            // Re-KDF salt keeps old pre-images useless.
            file.Kdf.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            file.Kdf.Mem = 262144;
            file.Kdf.Iter = 5;
            byte[] passwordKey = DerivePasswordKey(newPassword, file.Kdf);
            try { file.Wrap = SealToBlob(passwordKey, master); }
            finally { CryptographicOperations.ZeroMemory(passwordKey); }
        }

        file.Data = SealToBlob(master, Serialize(payload));

        // Atomic write: temp file + replace, so a crash can never corrupt the vault.
        string tmp = vaultPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
        if (File.Exists(vaultPath)) File.Replace(tmp, vaultPath, null);
        else File.Move(tmp, vaultPath);
    }

    /// <summary>Decrypt payload with a master key obtained from a seal. Throws if key is wrong.</summary>
    public static VaultPayload OpenPayload(VaultFile file, byte[] master)
        => Deserialize(OpenBlob(master, file.Data));

    // ---------- DPAPI seals (quick unlock on this PC only) ----------

    public static string CurrentMachineId()
    {
        string machine = Environment.MachineName;
        string domain = Environment.UserDomainName;
        string user = Environment.UserName;
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes($"{machine}|{domain}|{user}"));
        return Convert.ToHexString(h, 0, 8); // 16 hex chars
    }

    public static byte[]? UnsealLocal(VaultFile file, out SealEntry? matched)
    {
        string id = CurrentMachineId();
        foreach (var seal in file.Seals)
        {
            if (seal.MachineId != id) continue;
            try
            {
                byte[] master = ProtectedData.Unprotect(Convert.FromBase64String(seal.Dpapi), null,
                                                        DataProtectionScope.CurrentUser);
                // Sanity: master must open Data.
                _ = OpenBlob(master, file.Data);
                matched = seal;
                return master;
            }
            catch (CryptographicException) { }
            catch (InvalidDataException) { }
        }
        matched = null;
        return null;
    }

    public static SealEntry SealLocal(byte[] master)
    {
        return new SealEntry
        {
            MachineId = CurrentMachineId(),
            Dpapi = Convert.ToBase64String(
                ProtectedData.Protect(master, null, DataProtectionScope.CurrentUser))
        };
    }

    // ---------- serialization ----------

    private static byte[] Serialize(VaultPayload p) => JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts);

    private static VaultPayload Deserialize(byte[] json) =>
        JsonSerializer.Deserialize<VaultPayload>(json) ?? new VaultPayload();
}