using System.Net;
using System.Net.Sockets;

namespace RDPVault;

/// <summary>The separate Port field is authoritative, including for legacy host:port inputs.</summary>
public readonly record struct ConnectionEndpoint(string Host, int Port)
{
    public string Address => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public static ConnectionEndpoint FromProfile(RdpProfile profile) => Parse(profile.Host, profile.Port);

    public static ConnectionEndpoint Parse(string input, int port = 3389)
    {
        if (!TryParse(input, out var endpoint, out string error, port))
            throw new ArgumentException(error);
        return endpoint;
    }

    public static bool TryParse(string? input, out ConnectionEndpoint endpoint, out string error, int port = 3389)
    {
        endpoint = default;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the host name or IP address to connect to.";
            return false;
        }

        string host = input.Trim();
        int embedded = 0;

        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0 || !IPAddress.TryParse(host[1..end], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Enter a valid IPv6 address.";
                return false;
            }
            string suffix = host[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out embedded) || embedded is < 1 or > 65535)
                {
                    error = "Enter the computer address and use the Port field for its port.";
                    return false;
                }
            }
            host = host[1..end];
        }
        else if (host.Count(c => c == ':') == 1)
        {
            int colon = host.LastIndexOf(':');
            if (!int.TryParse(host[(colon + 1)..], out embedded) || embedded is < 1 or > 65535)
            {
                error = "Enter the computer address and use the Port field for its port.";
                return false;
            }
            host = host[..colon];
        }

        int finalPort = port;
        if (finalPort is < 1 or > 65535)
        {
            finalPort = embedded > 0 ? embedded : 3389;
        }
        // If the separate port is valid (1..65535), it takes precedence over embedded port.

        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '&', '?', '#', '"']) >= 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "Enter a valid computer name or IP address, without a URL or spaces.";
            return false;
        }

        endpoint = new ConnectionEndpoint(host, finalPort);
        return true;
    }
}
