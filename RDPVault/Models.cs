using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RDPVault;

public static class VaultMemoryGuard
{
    private static readonly byte[] SessionKey = RandomNumberGenerator.GetBytes(32);

    public static (byte[]? ct, byte[]? nonce, byte[]? tag) ProtectString(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return (null, null, null);
        byte[] pt = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] ct = new byte[pt.Length];
        byte[] tag = new byte[16];
        try
        {
            using var aes = new AesGcm(SessionKey, 16);
            aes.Encrypt(nonce, pt, ct, tag);
            return (ct, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pt);
        }
    }

    public static string UnprotectString(byte[]? ct, byte[]? nonce, byte[]? tag)
    {
        if (ct == null || nonce == null || tag == null || ct.Length == 0) return "";
        byte[] pt = new byte[ct.Length];
        try
        {
            using var aes = new AesGcm(SessionKey, 16);
            aes.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch
        {
            return "";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pt);
        }
    }
}

public enum TriStateOverride
{
    InheritGlobal = 0,
    Enabled = 1,
    Disabled = 2
}

public class RdpProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";

    private byte[]? _encPassword;
    private byte[]? _passwordNonce;
    private byte[]? _passwordTag;

    [JsonPropertyName("Password")]
    public string Password
    {
        get => VaultMemoryGuard.UnprotectString(_encPassword, _passwordNonce, _passwordTag);
        set => (_encPassword, _passwordNonce, _passwordTag) = VaultMemoryGuard.ProtectString(value);
    }

    public bool UseMultiMon { get; set; } = false;
    public string GatewayHost { get; set; } = "";
    public bool FullScreen { get; set; } = true;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public bool AllowClipboard { get; set; } = true;
    public bool AllowDrives { get; set; } = false;
    public bool AllowPrinters { get; set; } = false;
    public bool AllowSmartCards { get; set; } = false;

    public TriStateOverride SuppressCertWarningsOverride { get; set; } = TriStateOverride.InheritGlobal;

    public TriStateOverride FullScreenOverride { get; set; } = TriStateOverride.InheritGlobal;

    public TriStateOverride MultiMonOverride { get; set; } = TriStateOverride.InheritGlobal;

    public TriStateOverride AllowClipboardOverride { get; set; } = TriStateOverride.InheritGlobal;

    public bool AllowUnverifiedServer { get; set; } = false;

    public string CertThumbprint { get; set; } = "";

    public bool EnableWol { get; set; } = false;
    public string WolMacAddress { get; set; } = "";
    public string WolBroadcastIp { get; set; } = "255.255.255.255";
    public int WolPort { get; set; } = 9;
    public int WolWaitSeconds { get; set; } = 5;

    public bool EnableIcmpKnock { get; set; } = false;
    public string KnockProtocol { get; set; } = "ICMP";
    public int KnockTcpPort { get; set; } = 7777;
    public int KnockDelaySeconds { get; set; } = 2;
    public string IcmpKnockSignature { get; set; } = "";

    public string Notes { get; set; } = "";

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(Password);

    [JsonIgnore] public string DisplayHost => ConnectionEndpoint.FromProfile(this).Address;

    public string ResolutionPreset { get; set; } = "InheritGlobal";

    public bool SmartSizing { get; set; } = false;

    public TriStateOverride SmartSizingOverride { get; set; } = TriStateOverride.InheritGlobal;

    public bool ResolveSuppressCertWarnings(VaultSettings? settings)
    {
        if (SuppressCertWarningsOverride == TriStateOverride.Enabled) return true;
        if (SuppressCertWarningsOverride == TriStateOverride.Disabled) return false;
        return settings?.SuppressCertWarnings ?? true;
    }

    public bool ResolveFullScreen(VaultSettings? settings)
    {
        if (FullScreenOverride == TriStateOverride.Enabled) return true;
        if (FullScreenOverride == TriStateOverride.Disabled) return false;
        if (!FullScreen) return false;
        return settings?.DefaultFullScreen ?? FullScreen;
    }

    public bool ResolveUseMultiMon(VaultSettings? settings)
    {
        if (MultiMonOverride == TriStateOverride.Enabled) return true;
        if (MultiMonOverride == TriStateOverride.Disabled) return false;
        return settings?.DefaultUseMultiMon ?? UseMultiMon;
    }

    public bool ResolveAllowClipboard(VaultSettings? settings)
    {
        if (AllowClipboardOverride == TriStateOverride.Enabled) return true;
        if (AllowClipboardOverride == TriStateOverride.Disabled) return false;
        return settings?.DefaultAllowClipboard ?? AllowClipboard;
    }

    public bool ResolveSmartSizing(VaultSettings? settings)
    {
        if (SmartSizingOverride == TriStateOverride.Enabled) return true;
        if (SmartSizingOverride == TriStateOverride.Disabled) return false;
        return settings?.DefaultSmartSizing ?? SmartSizing;
    }

    public (int width, int height, bool isDeviceNative) ResolveResolution(VaultSettings? settings)
    {
        string preset = ResolutionPreset;
        if (string.Equals(preset, "InheritGlobal", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(preset))
        {
            preset = settings?.DefaultResolution ?? "1920x1080";
        }

        string clean = preset.Trim().ToLowerInvariant();
        var (w, h, native) = clean switch
        {
            "device" or "match device" or "native" => (1920, 1080, false),
            "1920x1080" or "1080p" => (1920, 1080, false),
            "1280x720" or "720p" => (1280, 720, false),
            "1600x900" or "900p" => (1600, 900, false),
            "1366x768" => (1366, 768, false),
            "2560x1440" or "1440p" or "2k" => (2560, 1440, false),
            "3840x2160" or "4k" => (3840, 2160, false),
            "custom" => (Width > 0 ? Width : 1920, Height > 0 ? Height : 1080, false),
            _ => (Width > 0 ? Width : (settings?.DefaultWidth > 0 ? settings.DefaultWidth : 1920),
                  Height > 0 ? Height : (settings?.DefaultHeight > 0 ? settings.DefaultHeight : 1080), false)
        };

        if (w < h && w > 0 && h > 0)
        {
            (w, h) = (h, w);
        }
        return (w, h, native);
    }

    public RdpProfile Clone() => (RdpProfile)MemberwiseClone();
}

public enum SweepScope
{
    OwnHostsOnly = 0,
    Everything = 1
}

public class VaultSettings
{
    public int LockMinutes { get; set; } = 60;

    public bool LockImmediatelyOnBackground { get; set; } = false;
    public bool KillSessionsOnUsbRemoval { get; set; } = true;
    public bool ForceMultiMon { get; set; } = false;

    public bool SuppressCertWarnings { get; set; } = true;

    public bool DefaultFullScreen { get; set; } = true;

    public bool DefaultUseMultiMon { get; set; } = true;

    public bool DefaultAllowClipboard { get; set; } = true;

    public string DefaultResolution { get; set; } = "1920x1080";

    public int DefaultWidth { get; set; } = 1920;

    public int DefaultHeight { get; set; } = 1080;

    public bool DefaultSmartSizing { get; set; } = false;

    public bool DeepSweep { get; set; } = false;

    public SweepScope SweepScope { get; set; } = SweepScope.OwnHostsOnly;

    public bool WarnIfDriveNotEncrypted { get; set; } = false;
}

public class VaultPayload
{
    public List<RdpProfile> Profiles { get; set; } = new();
    public VaultSettings Settings { get; set; } = new();
    public string PendingRecoveryCode { get; set; } = "";
}

public class SealEntry
{
    public string MachineId { get; set; } = "";
    public string TpmBlob { get; set; } = "";
    public string KeyId { get; set; } = "";
}

public class VaultPolicy
{
    public bool SelfDestructEnabled { get; set; } = false;

    private int _maxAttempts = 25;
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
    public int V { get; set; } = 2;

    public KdfParams Kdf { get; set; } = new();
    public WrappedBlob Wrap { get; set; } = new();

    public WrappedBlob? Recovery { get; set; }

    public string RecoverySalt { get; set; } = "";

    public List<SealEntry> Seals { get; set; } = new();
    public VaultPolicy Policy { get; set; } = new();
    public FailState Fails { get; set; } = new();
    public WrappedBlob Data { get; set; } = new();

    public class KdfParams
    {
        public string Salt { get; set; } = "";
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
[JsonSerializable(typeof(TriStateOverride))]
[JsonSerializable(typeof(SweepScope))]
public partial class VaultJsonContext : JsonSerializerContext
{
}

public static class MacAddressHelper
{
    public static bool TryNormalizeMac(string? input, out string formattedMac, out string error)
    {
        formattedMac = "";
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "MAC address cannot be empty.";
            return false;
        }

        string trimmed = input.Trim();

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (!Uri.IsHexDigit(c) && c != ':' && c != '-' && c != '.' && c != ' ')
            {
                error = $"Invalid character '{c}' in MAC address. Only hexadecimal digits (0-9, A-F) and separators (:, -, .) are allowed.";
                return false;
            }
        }

        var hexChars = trimmed.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray();
        if (hexChars.Length != 12)
        {
            error = $"A valid MAC address must contain exactly 12 hexadecimal characters (found {hexChars.Length}). Example: 00:11:22:33:44:55.";
            return false;
        }

        formattedMac = string.Join(":", Enumerable.Range(0, 6).Select(i => new string(hexChars, i * 2, 2)));
        return true;
    }
}
