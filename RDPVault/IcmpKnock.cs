using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RDPVault;

/// <summary>
/// Port Knocking helper supporting ICMP magic packets and TCP port knocks with configurable delay.
/// </summary>
public static class IcmpKnock
{
    public const int DefaultDelayMilliseconds = 2000;

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

    public static async Task SendBeforeConnectAsync(RdpProfile profile, CancellationToken ct)
    {
        await SendBeforeConnectAsync(profile, null, ct).ConfigureAwait(false);
    }

    public static async Task SendBeforeConnectAsync(
        RdpProfile profile,
        Func<string, byte[], CancellationToken, Task>? customIcmpSend,
        CancellationToken ct)
    {
        if (!profile.EnableIcmpKnock) return;
        ct.ThrowIfCancellationRequested();

        string host = ConnectionEndpoint.FromProfile(profile).Host;
        int delayMs = (profile.KnockDelaySeconds >= 0 ? profile.KnockDelaySeconds : 2) * 1000;

        if (string.Equals(profile.KnockProtocol, "TCP", StringComparison.OrdinalIgnoreCase))
        {
            int tcpPort = profile.KnockTcpPort is >= 1 and <= 65535 ? profile.KnockTcpPort : 7777;
            await SendTcpKnockAsync(host, tcpPort, ct).ConfigureAwait(false);
        }
        else
        {
            byte[] bytes = ParseSignature(profile.IcmpKnockSignature);
            try
            {
                if (customIcmpSend != null)
                {
                    await customIcmpSend(host, bytes, ct).ConfigureAwait(false);
                }
                else
                {
                    await SendIcmpAsync(host, bytes, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    public static async Task SendBeforeConnectAsync(string host, string signature, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] bytes = ParseSignature(signature);
        try
        {
            await SendIcmpAsync(host, bytes, ct).ConfigureAwait(false);
            await Task.Delay(DefaultDelayMilliseconds, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static async Task SendIcmpAsync(string host, byte[] payload, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            // Firewalls often consume a knock without replying; a timeout is expected.
            await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(750), payload, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (PingException)
        {
            // Expected on networks that block ICMP responses
        }
        catch (SocketException)
        {
            // Expected
        }
    }

    public static Task SendWindowsAsync(string host, byte[] payload, CancellationToken ct) => SendIcmpAsync(host, payload, ct);

    public static async Task SendTcpKnockAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // A knock port is typically closed or firewalled - the TCP SYN/connection attempt itself is the knock.
        }
    }
}
