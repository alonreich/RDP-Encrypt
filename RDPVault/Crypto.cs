using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;

namespace RDPVault;

public static class VaultCrypto
{
    private const int MasterKeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int SaltBytes = 32;

    // ---------------------------------------------------------------- KDF

    /// <summary>
    /// Issue #19: the Argon2 instance is now disposed and the UTF-8 copy of the
    /// secret is zeroed in a finally block. Previously both were left in memory.
    /// </summary>
    private static byte[] DeriveKey(string secret, byte[] salt, int memKiB, int iterations, int lanes)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        Argon2? argon2 = null;
        try
        {
            var config = new Argon2Config
            {
                Type = Argon2Type.HybridAddressing,   // == Argon2id
                Version = Argon2Version.Nineteen,     // v1.3
                MemoryCost = memKiB,
                TimeCost = iterations,
                Lanes = lanes,
                Threads = lanes,
                Password = secretBytes,
                Salt = salt,
                HashLength = MasterKeyBytes
            };
            argon2 = new Argon2(config);
            using var hash = argon2.Hash();
            byte[] key = new byte[MasterKeyBytes];
            Array.Copy(hash.Buffer, key, MasterKeyBytes);
            return key;
        }
        finally
        {
            ((argon2 as object) as IDisposable)?.Dispose();
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public static byte[] DerivePasswordKey(string password, VaultFile.KdfParams kdf)
        => DeriveKey(password, Convert.FromBase64String(kdf.Salt), kdf.Mem, kdf.Iter, kdf.Lanes);

    /// <summary>Recovery codes are high-entropy already, so a lighter (but still memory-hard) KDF is fine.</summary>
    private static byte[] DeriveRecoveryKey(string normalizedCode, string saltB64)
        => DeriveKey(normalizedCode, Convert.FromBase64String(saltB64), 65536, 3, 4);

    /// <summary>
    /// Issue #18c: derived from the raw TPM signature. Enrollment verifies the
    /// signature is reproducible before trusting this (see WindowsHello).
    /// </summary>
    public static byte[] DeriveTpmKey(byte[] signature, string saltBase64)
    {
        Argon2? argon2 = null;
        try
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
            argon2 = new Argon2(config);
            using var hash = argon2.Hash();
            byte[] key = new byte[MasterKeyBytes];
            Array.Copy(hash.Buffer, key, MasterKeyBytes);
            return key;
        }
        finally
        {
            ((argon2 as object) as IDisposable)?.Dispose();
        }
    }

    // ---------------------------------------------------------------- AEAD

    private static (byte[] nonce, byte[] ct) AeadSeal(byte[] key, byte[] plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] ct = new byte[plaintext.Length + TagBytes];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, plaintext, ct.AsSpan(0, plaintext.Length), ct.AsSpan(plaintext.Length));
        return (nonce, ct);
    }

    private static byte[] AeadOpen(byte[] key, byte[] nonce, byte[] ct)
    {
        if (ct.Length < TagBytes) throw new CryptographicException("Blob too short.");
        byte[] pt = new byte[ct.Length - TagBytes];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(nonce, ct.AsSpan(0, ct.Length - TagBytes), ct.AsSpan(ct.Length - TagBytes), pt);
        return pt;
    }

    private static VaultFile.WrappedBlob SealToBlob(byte[] key, byte[] data)
    {
        var (nonce, ct) = AeadSeal(key, data);
        return new VaultFile.WrappedBlob { Nonce = Convert.ToBase64String(nonce), Ct = Convert.ToBase64String(ct) };
    }

    private static byte[] OpenBlob(byte[] key, VaultFile.WrappedBlob blob)
        => AeadOpen(key, Convert.FromBase64String(blob.Nonce), Convert.FromBase64String(blob.Ct));

    // ---------------------------------------------------------------- create / open

    /// <summary>
    /// Creates a brand new vault. Issue #2: a Recovery Code is generated and bound
    /// to the master key at creation time - it is a real second way in, not decoration.
    /// The caller MUST show <paramref name="recoveryCodeDisplay"/> to the user.
    /// </summary>
    public static VaultFile CreateVault(string password, VaultPayload payload, string vaultPath,
                                        out string recoveryCodeDisplay)
    {
        var file = new VaultFile();
        file.Kdf.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));
        file.RecoverySalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));

        byte[] master = RandomNumberGenerator.GetBytes(MasterKeyBytes);
        byte[]? passwordKey = null;
        byte[]? recoveryKey = null;
        try
        {
            passwordKey = DerivePasswordKey(password, file.Kdf);
            file.Wrap = SealToBlob(passwordKey, master);

            recoveryCodeDisplay = RecoveryCode.Generate();
            recoveryKey = DeriveRecoveryKey(RecoveryCode.Normalize(recoveryCodeDisplay), file.RecoverySalt);
            file.Recovery = SealToBlob(recoveryKey, master);

            file.Data = SealToBlob(master, Serialize(payload));
            WriteAtomic(file, vaultPath);
            return file;
        }
        finally
        {
            if (passwordKey != null) CryptographicOperations.ZeroMemory(passwordKey);
            if (recoveryKey != null) CryptographicOperations.ZeroMemory(recoveryKey);
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>Unlock with the master password.</summary>
    public static (byte[] Master, VaultPayload Payload) Open(VaultFile file, string password)
    {
        byte[] passwordKey = DerivePasswordKey(password, file.Kdf);
        byte[]? master = null;
        try
        {
            try { master = OpenBlob(passwordKey, file.Wrap); }
            catch (CryptographicException) { throw new InvalidDataException("Wrong password."); }

            VaultPayload payload = OpenPayloadOrThrow(file, master);
            byte[] owned = master;
            master = null; // ownership transferred to the caller
            return (owned, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
            // Issue #19: if payload decoding threw, the master key no longer leaks.
            if (master != null) CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>Issue #2: unlock with the printed Recovery Code. Returns null if the code is wrong.</summary>
    public static (byte[] Master, VaultPayload Payload)? OpenWithRecoveryCode(VaultFile file, string typedCode)
    {
        if (file.Recovery == null || string.IsNullOrEmpty(file.RecoverySalt)) return null;

        string normalized = RecoveryCode.Normalize(typedCode);
        if (normalized.Length < 16) return null;

        byte[] recoveryKey = DeriveRecoveryKey(normalized, file.RecoverySalt);
        byte[]? master = null;
        try
        {
            try { master = OpenBlob(recoveryKey, file.Recovery); }
            catch (CryptographicException) { return null; }

            VaultPayload payload = OpenPayloadOrThrow(file, master);
            byte[] owned = master;
            master = null;
            return (owned, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
            if (master != null) CryptographicOperations.ZeroMemory(master);
        }
    }

    public static VaultPayload OpenPayload(VaultFile file, byte[] master) => OpenPayloadOrThrow(file, master);

    /// <summary>
    /// Issue #19: JsonException used to escape uncaught with a developer-facing
    /// message. Both failure modes now produce one clear sentence.
    /// </summary>
    private static VaultPayload OpenPayloadOrThrow(VaultFile file, byte[] master)
    {
        byte[] plain;
        try { plain = OpenBlob(master, file.Data); }
        catch (CryptographicException)
        {
            throw new InvalidDataException(
                "This vault file is damaged and could not be decrypted. Restore the .bak copy next to it.");
        }

        try
        {
            return JsonSerializer.Deserialize(plain, VaultJsonContext.Default.VaultPayload) ?? new VaultPayload();
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                "This vault decrypted but its contents are unreadable. Restore the .bak copy next to it.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    // ---------------------------------------------------------------- save

    public static void Save(VaultFile file, byte[] master, VaultPayload payload, string vaultPath,
                            string? newPassword = null, List<SealEntry>? newSeals = null,
                            bool regenerateRecovery = false, Action<string>? recoveryCodeOut = null)
    {
        if (newSeals != null) file.Seals = newSeals;

        if (newPassword != null)
        {
            // Re-salt on every password change, and drop all quick-unlock seals:
            // a password change is treated as a possible compromise.
            file.Kdf.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));
            byte[] passwordKey = DerivePasswordKey(newPassword, file.Kdf);
            try { file.Wrap = SealToBlob(passwordKey, master); }
            finally { CryptographicOperations.ZeroMemory(passwordKey); }
        }

        if (regenerateRecovery)
        {
            file.RecoverySalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));
            string code = RecoveryCode.Generate();
            byte[] recoveryKey = DeriveRecoveryKey(RecoveryCode.Normalize(code), file.RecoverySalt);
            try { file.Recovery = SealToBlob(recoveryKey, master); }
            finally { CryptographicOperations.ZeroMemory(recoveryKey); }
            recoveryCodeOut?.Invoke(code);
        }

        file.V = 2;
        file.Data = SealToBlob(master, Serialize(payload));
        WriteAtomic(file, vaultPath);
    }

    /// <summary>
    /// Rewrites only the unencrypted envelope (policy / failure counter). Used while
    /// the vault is LOCKED, so no master key is available. Wrap/Recovery/Data are
    /// carried over untouched.
    /// </summary>
    public static void SaveEnvelopeOnly(VaultFile file, string vaultPath) => WriteAtomic(file, vaultPath);

    /// <summary>
    /// Atomic write + rolling backup. Issue #1/#4: there is now always a previous
    /// good copy at vault.rdpv.bak, so a bad write or an accidental wipe is survivable.
    /// </summary>
    private static void WriteAtomic(VaultFile file, string vaultPath)
    {
        string json = JsonSerializer.Serialize(file, VaultJsonContext.Default.VaultFile);
        string tmp = vaultPath + AppPaths.TempSuffix;
        string bak = vaultPath + AppPaths.BackupSuffix;

        File.WriteAllText(tmp, json);
        if (File.Exists(vaultPath))
        {
            try { File.Copy(vaultPath, bak, overwrite: true); } catch { /* backup is best effort */ }
            File.Replace(tmp, vaultPath, null);
        }
        else
        {
            File.Move(tmp, vaultPath);
        }
    }

    // ---------------------------------------------------------------- machine binding

    public static string CurrentMachineId()
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Environment.MachineName}|{Environment.UserDomainName}|{Environment.UserName}"));
        return Convert.ToHexString(h, 0, 8);
    }

    public static SealEntry SealTpm(byte[] master, string keyId, byte[] signature, VaultFile file)
    {
        byte[] tpmKey = DeriveTpmKey(signature, file.Kdf.Salt);
        try
        {
            var blob = SealToBlob(tpmKey, master);
            return new SealEntry
            {
                MachineId = CurrentMachineId(),
                KeyId = keyId,
                TpmBlob = $"{blob.Nonce}:{blob.Ct}"
            };
        }
        finally { CryptographicOperations.ZeroMemory(tpmKey); }
    }

    public static byte[]? UnsealTpm(VaultFile file, SealEntry seal, byte[] signature)
    {
        byte[] tpmKey = DeriveTpmKey(signature, file.Kdf.Salt);
        try
        {
            string[] parts = seal.TpmBlob.Split(':');
            if (parts.Length != 2) return null;
            return OpenBlob(tpmKey, new VaultFile.WrappedBlob { Nonce = parts[0], Ct = parts[1] });
        }
        catch { return null; }
        finally { CryptographicOperations.ZeroMemory(tpmKey); }
    }

    // ---------------------------------------------------------------- json

    private static byte[] Serialize(VaultPayload p)
        => JsonSerializer.SerializeToUtf8Bytes(p, VaultJsonContext.Default.VaultPayload);
}
