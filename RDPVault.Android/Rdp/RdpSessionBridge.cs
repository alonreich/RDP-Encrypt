using System;

namespace RDPVault.Android.Rdp;

/// <summary>
/// In-memory bridge for passing connection parameters to RdpSessionActivity and managing
/// the active session lifecycle without touching Android Intent extras, Logcat, or disk storage.
/// </summary>
public static class RdpSessionBridge
{
    private static readonly object _lock = new();
    private static RdpSessionConfig? _pendingConfig;

    public static RdpSessionConfig? ConsumePendingConfig()
    {
        lock (_lock)
        {
            var cfg = _pendingConfig;
            _pendingConfig = null;
            return cfg;
        }
    }

    public static void SetPendingConfig(RdpSessionConfig config)
    {
        lock (_lock)
        {
            _pendingConfig = config;
        }
    }

    public static bool HasPendingConfig
    {
        get
        {
            lock (_lock)
            {
                return _pendingConfig != null;
            }
        }
    }

    public static FreeRdpSession? ActiveSession { get; set; }
    public static bool IsConnected { get; set; }
    public static RdpProfile? ConnectedProfile { get; set; }
    public static Action? SessionStateChanged { get; set; }
}

public class RdpSessionConfig
{
    public RdpProfile? Profile { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public char[]? PasswordChars { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool AllowClipboard { get; set; } = false;
    public bool SuppressCertWarnings { get; set; } = true;
    public string? PinnedFingerprint { get; set; }
    public string ProfileName { get; set; } = "Remote PC";
    public string? GatewayHost { get; set; }
    public int GatewayPort { get; set; } = 443;
}
