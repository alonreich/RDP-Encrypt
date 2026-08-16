using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RDPVault;

public class RdpProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseMultiMon { get; set; } = false;
    public string GatewayHost { get; set; } = "";
    public bool FullScreen { get; set; } = true;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public bool AllowClipboard { get; set; } = true;
    public bool AllowDrives { get; set; } = false;
    public bool AllowPrinters { get; set; } = false;
    public bool AllowSmartCards { get; set; } = false;

    /// <summary>
    /// Issue #10: the generated .rdp used to hard-code "authentication level:i:0",
    /// which silences mstsc's server-identity check entirely. The default is now 2
    /// (refuse to connect if the server cannot be verified). Tick this only for a
    /// host you know uses a self-signed certificate - it downgrades to 1 (warn),
    /// never back to 0.
    /// </summary>
    public bool AllowUnverifiedServer { get; set; } = false;

    public string Notes { get; set; } = "";

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(Password);

    // Issue #8: this used to be the literal string "$Host:$Port" (missing the
    // interpolation prefix), so every non-3389 profile rendered that garbage.
    [JsonIgnore] public string DisplayHost => Port == 3389 ? Host : $"{Host}:{Port}";

    public RdpProfile Clone() => (RdpProfile)MemberwiseClone();
}

/// <summary>How aggressively TraceCleaner is allowed to delete (issue #20).</summary>
public enum SweepScope
{
    /// <summary>Only remove traces for hosts this vault manages. Default.</summary>
    OwnHostsOnly = 0,
    /// <summary>Remove every mstsc trace on the machine, including connections made outside this app.</summary>
    Everything = 1
}

public class VaultSettings
{
    public int LockMinutes { get; set; } = 60;
    public bool KillSessionsOnUsbRemoval { get; set; } = true;
    public bool ForceMultiMon { get; set; } = false;

    /// <summary>Issue #7: now actually honoured - DeepSweep runs on lock and exit when true.</summary>
    public bool DeepSweep { get; set; } = false;

    /// <summary>Issue #20: default is to leave the user's own mstsc history alone.</summary>
    public SweepScope SweepScope { get; set; } = SweepScope.OwnHostsOnly;

    /// <summary>Issue #7: warn (never silently pretend) when the vault drive is not encrypted.</summary>
    public bool WarnIfDriveNotEncrypted { get; set; } = true;

    // NOTE (issue #7): "RequireFido2" was removed. It was stored and displayed but
    // never enforced by any code path, which actively misled users.
}

public class VaultPayload
{
    public List<RdpProfile> Profiles { get; set; } = new();
    public VaultSettings Settings { get; set; } = new();
}

public class SealEntry
{
    public string MachineId { get; set; } = "";
    public string TpmBlob { get; set; } = "";
    public string KeyId { get; set; } = "";
}

/// <summary>
/// Brute-force policy. Lives UNENCRYPTED inside vault.rdpv on purpose (issue #4):
/// it must be readable while the vault is locked, and putting it in the vault file
/// means an attacker cannot reset the counter without touching the very file they
/// are attacking. It contains no secrets.
/// </summary>
public class VaultPolicy
{
    /// <summary>OFF by default. Destroying the user's only copy of their credentials
    /// is never a safe default (issue #4).</summary>
    public bool SelfDestructEnabled { get; set; } = false;

    private int _maxAttempts = 25;
    /// <summary>Clamped 5..500 so a mistyped "0" can never nuke the vault on attempt one.</summary>
    public int MaxAttempts
    {
        get => Math.Clamp(_maxAttempts, 5, 500);
        set => _maxAttempts = Math.Clamp(value, 5, 500);
    }

    private int _windowMinutes = 60;
    public int WindowMinutes
    {
        get => Math.Clamp(_windowMinutes, 1, 10080);
        set => _windowMinutes = Math.Clamp(value, 1, 10080);
    }

    /// <summary>Always-on, non-destructive brute-force defence.</summary>
    public bool ThrottleEnabled { get; set; } = true;
}

public class FailState
{
    public int Count { get; set; } = 0;
    public DateTime FirstFailUtc { get; set; } = DateTime.MinValue;
    public DateTime LastFailUtc { get; set; } = DateTime.MinValue;
}

public class VaultFile
{
    /// <summary>1 = original format. 2 = adds Recovery / Policy / Fails.</summary>
    public int V { get; set; } = 2;

    public KdfParams Kdf { get; set; } = new();
    public WrappedBlob Wrap { get; set; } = new();

    /// <summary>masterKey wrapped under the printed Recovery Code. Null if the user has none yet.</summary>
    public WrappedBlob? Recovery { get; set; }

    /// <summary>Independent salt for the recovery-code KDF (never reuses the password salt).</summary>
    public string RecoverySalt { get; set; } = "";

    public List<SealEntry> Seals { get; set; } = new();
    public VaultPolicy Policy { get; set; } = new();
    public FailState Fails { get; set; } = new();
    public WrappedBlob Data { get; set; } = new();

    public class KdfParams
    {
        public string Salt { get; set; } = "";
        // Issue #13: was 262144 KiB / t=5, which meant multi-second unlocks and a
        // 256 MB spike on the managed Argon2 implementation. 64 MiB / t=3 matches
        // the documented spec and is still memory-hard. Existing vaults keep their
        // own stored parameters, so this change is backward compatible.
        public int Mem { get; set; } = 65536;
        public int Iter { get; set; } = 3;
        public int Lanes { get; set; } = 4;
    }

    public class WrappedBlob
    {
        public string Nonce { get; set; } = "";
        public string Ct { get; set; } = "";
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VaultFile))]
[JsonSerializable(typeof(VaultPayload))]
[JsonSerializable(typeof(FailState))]
[JsonSerializable(typeof(VaultPolicy))]
public partial class VaultJsonContext : JsonSerializerContext
{
}
