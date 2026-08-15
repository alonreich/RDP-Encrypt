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
    public bool AllowClipboard { get; set; } = false;
    public bool AllowDrives { get; set; } = false;
    public bool AllowPrinters { get; set; } = false;
    public bool AllowSmartCards { get; set; } = false;
    public string Notes { get; set; } = "";

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(Password);
    [JsonIgnore] public string DisplayHost => Port == 3389 ? Host : "$Host:$Port";
}

public class VaultSettings
{
    public int LockMinutes { get; set; } = 60;
    public bool DeepSweep { get; set; } = true;
    public bool KillSessionsOnUsbRemoval { get; set; } = true;
    public int SelfDestructFailedAttempts { get; set; } = 20;
    public int SelfDestructWindowMinutes { get; set; } = 60;
    public bool RequireFido2 { get; set; } = false;
    public bool EnforceBitLocker { get; set; } = true;
    public bool ForceMultiMon { get; set; } = false;
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

public class VaultFile
{
    public int V { get; set; } = 1;

    public KdfParams Kdf { get; set; } = new();
    public WrappedBlob Wrap { get; set; } = new();
    public List<SealEntry> Seals { get; set; } = new();
    public WrappedBlob Data { get; set; } = new();

    public class KdfParams
    {
        public string Salt { get; set; } = "";
        public int Mem { get; set; } = 262144;
        public int Iter { get; set; } = 5;
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
[JsonSerializable(typeof(SecurityEnforcer.FailState))]
public partial class VaultJsonContext : JsonSerializerContext
{
}
