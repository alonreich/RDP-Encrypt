using System.Net.NetworkInformation;
using System.Security.Cryptography;

namespace RDPVault;

/// <summary>An optional ICMP Echo payload. This is not authentication and ICMP has no port.</summary>
public static class IcmpKnock
{
    public const int DelayMilliseconds = 2000;
    public static string GenerateSignature() => Convert.ToHexString(RandomNumberGenerator.GetBytes(GenerateLength()));

    private static int GenerateLength()
    {
        int length;
        do { length = RandomNumberGenerator.GetInt32(97, 224); } while (length % 16 == 0);
        return length;
    }

    public static byte[] ParseSignature(string signature)
    {
        string hex = string.Concat(signature.Where(c => !char.IsWhiteSpace(c) && c != '-'));
        if (hex.Length < 34 || hex.Length > 2048 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("Enter 17–1024 bytes as pairs of hexadecimal characters (0–9, A–F), or generate a signature.");
        return Convert.FromHexString(hex);
    }

    public static async Task SendBeforeConnectAsync(RdpProfile profile, Func<string, byte[], CancellationToken, Task> send, CancellationToken ct)
    {
        if (!profile.EnableIcmpKnock) return;
        byte[] bytes = ParseSignature(profile.IcmpKnockSignature);
        try
        {
            ct.ThrowIfCancellationRequested();
            await send(ConnectionEndpoint.FromProfile(profile).Host, bytes, ct).ConfigureAwait(false);
            await Task.Delay(DelayMilliseconds, ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static async Task SendWindowsAsync(string host, byte[] payload, CancellationToken ct)
    {
        using var ping = new Ping();
        // No reply is required: firewalls commonly consume a knock without replying.
        await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(750), payload, cancellationToken: ct).ConfigureAwait(false);
    }
}
