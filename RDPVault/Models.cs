using System.Text.Json.Serialization;

namespace RDPVault;

/// <summary>One saved remote computer ("profile"). Stored only inside the encrypted vault.</summary>
public class RdpProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";          // IP or DNS name
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";      // may include DOMAIN\user or user@domain
    public string Password { get; set; } = "";
    public bool UseMultiMon { get; set; } = false;      // optional; empty = Windows asks at connect time
    public string GatewayHost { get; set; } = "";   // optional RD gateway
    public bool FullScreen { get; set; } = true;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public bool AllowClipboard { get; set; } = false;
    public bool AllowDrives { get; set; } = false;
    public bool AllowPrinters { get; set; } = false;
    public bool AllowSmartCards { get; set; } = false;
    public string Notes { get; set; } = "";

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(Password);
    [JsonIgnore] public string DisplayHost => Port == 3389 ? Host : $"{Host}:{Port}";
}

/// <summary>App preferences, stored inside the encrypted vault.</summary>
public class VaultSettings
{
    public int LockMinutes { get; set; } = 60;
    /// <summary>During cleanup also remove ANY saved RDP (TERMSRV/*) credentials on this PC.</summary>
    public bool DeepSweep { get; set; } = true;
    /// <summary>When the USB stick is pulled, also kill RDP sessions the app launched.</summary>
    public bool KillSessionsOnUsbRemoval { get; set; } = true;
    public int SelfDestructFailedAttempts { get; set; } = 20;
    public int SelfDestructWindowMinutes { get; set; } = 60;
    public bool RequireFido2 { get; set; } = false;
    public bool EnforceBitLocker { get; set; } = true;
    public bool ForceMultiMon { get; set; } = false;
}

/// <summary>The decrypted contents of the vault.</summary>
public class VaultPayload
{
    public List<RdpProfile> Profiles { get; set; } = new();
    public VaultSettings Settings { get; set; } = new();
}

/// <summary>
/// A "quick unlock" seal: the master key, protected with Windows DPAPI so only the
/// SAME user on the SAME PC can unseal it, bound to that PC's Windows Hello key.
/// Seals from other PCs ride along in the vault file but are useless there.
/// </summary>
public class SealEntry
{
    public string MachineId { get; set; } = "";  // first 16 hex chars of SHA-256(machine|domain|user)
    public string Dpapi { get; set; } = "";      // base64 DPAPI(CurrentUser)-protected master key
    public string KeyId { get; set; } = "";      // hex SHA-256 of the Windows Hello key's public blob
}

/// <summary>On-disk vault file format (outer JSON; the payload itself is AES-GCM encrypted).</summary>
public class VaultFile
{
    public int V { get; set; } = 1;

    public KdfParams Kdf { get; set; } = new();
    public WrappedBlob Wrap { get; set; } = new();          // master key wrapped by password key
    public List<SealEntry> Seals { get; set; } = new();
    public WrappedBlob Data { get; set; } = new();          // payload encrypted with the master key

    public class KdfParams
    {
        public string Salt { get; set; } = "";              // base64
        public int Mem { get; set; } = 262144;              // KiB (256 MiB) Argon2id memory
        public int Iter { get; set; } = 5;
        public int Lanes { get; set; } = 4;
    }

    public class WrappedBlob
    {
        public string Nonce { get; set; } = "";             // base64
        public string Ct { get; set; } = "";                // base64 ciphertext||tag
    }
}
