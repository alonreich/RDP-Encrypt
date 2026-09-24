using System.Net;
using System.Net.Sockets;

namespace RDPVault;

/// <summary>The separate Port field is authoritative, including for legacy host:port inputs.</summary>
public readonly record struct ConnectionEndpoint(string Host, int Port)
{
    public string Address => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public static ConnectionEndpoint FromProfile(RdpProfile profile) => Parse(profile.Host, profile.Port);

    public static ConnectionEndpoint Parse(string input, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        string host = input.Trim();
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0 || !IPAddress.TryParse(host[1..end], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("Enter a valid IPv6 address.");
            string suffix = host[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out int embedded) || embedded is < 1 or > 65535)
                    throw new ArgumentException("Enter the computer address and use the Port field for its port.");
                if (embedded > 0) port = embedded;
            }
            host = host[1..end];
        }
        else if (host.Count(c => c == ':') == 1)
        {
            int colon = host.LastIndexOf(':');
            if (!int.TryParse(host[(colon + 1)..], out int embedded) || embedded is < 1 or > 65535)
                throw new ArgumentException("Enter the computer address and use the Port field for its port.");
            host = host[..colon];
            if (embedded > 0) port = embedded;
        }
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '&', '?', '#', '"']) >= 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter a valid computer name or IP address, without a URL or spaces.");
        return new ConnectionEndpoint(host, port);
    }
}
