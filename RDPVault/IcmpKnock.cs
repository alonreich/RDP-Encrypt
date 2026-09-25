using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        // Emulates: curl -m 1 http://<host>:<port> >nul 2>&1
        // Dispatches both an HTTP GET request via HttpClient and direct dual-stack TCP SYN attempts
        // to guarantee compatibility across cellular CLAT/NAT64, Web knock daemons, and raw firewall SYN filters.
        var httpTask = SendHttpKnockAsync(host, port, ct);
        var synTask = SendSocketSynKnockAsync(host, port, ct);

        try
        {
            await Task.WhenAll(httpTask, synTask).ConfigureAwait(false);
        }
        catch
        {
            // All timeouts, connection refusals, and socket resets are expected during stealth port knocking.
        }
    }

    private static async Task SendHttpKnockAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(2500));

            string hostAuthority = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
            string url = $"http://{hostAuthority}:{port}/";

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMilliseconds(2500);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "curl/8.0");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            http.DefaultRequestHeaders.ConnectionClose = true;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Expected on stealth firewalls with drop rules (e.g. MikroTik action=drop after address-list)
        }
    }

    private static async Task SendSocketSynKnockAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(2500));

            var addresses = new List<IPAddress>();
            if (IPAddress.TryParse(host, out var directIp))
            {
                addresses.Add(directIp);
            }

            try
            {
                var resolved = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
                foreach (var r in resolved)
                {
                    if (!addresses.Any(a => a.Equals(r)))
                    {
                        addresses.Add(r);
                    }
                }
            }
            catch
            {
                // DNS resolution may fail if offline or IP literal without DNS64
            }

            // Also include direct host connect task using .NET Happy Eyeballs
            var tasks = new List<Task>();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                    if (socket.Connected)
                    {
                        string hostHeader = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
                        string httpRequest = $"GET / HTTP/1.1\r\nHost: {hostHeader}:{port}\r\nUser-Agent: curl/8.0\r\nAccept: */*\r\nConnection: close\r\n\r\n";
                        byte[] requestBytes = System.Text.Encoding.ASCII.GetBytes(httpRequest);
                        await socket.SendAsync(requestBytes, SocketFlags.None, cts.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Expected on drop firewall
                }
            }, cts.Token));

            foreach (var ip in addresses)
            {
                var targetIp = ip;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true
                        };
                        await socket.ConnectAsync(new IPEndPoint(targetIp, port), cts.Token).ConfigureAwait(false);

                        if (socket.Connected)
                        {
                            string hostHeader = targetIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{targetIp}]" : targetIp.ToString();
                            string httpRequest = $"GET / HTTP/1.1\r\nHost: {hostHeader}:{port}\r\nUser-Agent: curl/8.0\r\nAccept: */*\r\nConnection: close\r\n\r\n";
                            byte[] requestBytes = System.Text.Encoding.ASCII.GetBytes(httpRequest);
                            await socket.SendAsync(requestBytes, SocketFlags.None, cts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected: Stealth DROP firewall behavior causes timeout while SYN is delivered.
                    }
                    catch (SocketException)
                    {
                        // Expected: Reset or connection refused by intermediate hops still delivers SYN.
                    }
                    catch
                    {
                    }
                }, cts.Token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Expected
        }
    }
}
