using System.Net.NetworkInformation;

namespace MGGX.PCAgent.Core;

public sealed record LanAdapterCandidate(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool OperationalUp,
    bool HasDefaultGateway,
    string Ipv4Address,
    string? SubnetMask,
    string? MacAddress);

public sealed record NetworkAdapterOption(string Id, string DisplayName, string Ipv4Address);

public sealed record NetworkSnapshot(
    string? LanIp,
    string? BroadcastAddress,
    string? MacAddress,
    string? AdapterName,
    string? TailscaleIp,
    IReadOnlyList<NetworkAdapterOption> AvailableAdapters);

/// <summary>
/// Pure, testable adapter selection and address math. Excludes virtual adapters (WSL/Hyper-V/VMware/
/// VirtualBox/Docker/Tailscale/Bluetooth/tunnels/loopback) so the LAN IP shown to the user and embedded
/// in the pairing QR is a real physical network, not a virtual one.
/// </summary>
public static class NetworkSelector
{
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vmware", "virtualbox", "vbox", "hyper-v", "hyperv", "vethernet", "wsl", "docker",
        "tailscale", "tap-windows", "tap0", "tun0", "npcap", "loopback", "bluetooth", "vpn", "utun", "ppp",
        "wan miniport", "teredo", "isatap", "pseudo-interface"
    ];

    public static bool IsVirtualAdapter(string name, string description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return true;
        var text = $"{name} {description}".ToLowerInvariant();
        return VirtualMarkers.Any(text.Contains);
    }

    public static bool IsApipa(string ipv4) => ipv4.StartsWith("169.254.", StringComparison.Ordinal);

    public static bool IsPrivateLan(string ipv4)
    {
        var octets = TryParseOctets(ipv4);
        if (octets is null) return false;
        var (a, b, _, _) = octets.Value;
        return a == 10 || (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168);
    }

    public static bool IsTailscaleIp(string ipv4)
    {
        var octets = TryParseOctets(ipv4);
        if (octets is null) return false;
        var (a, b, _, _) = octets.Value;
        return a == 100 && b is >= 64 and <= 127;
    }

    /// <summary>Loopback, RFC1918 LAN, or Tailscale CGNAT (100.64.0.0/10). Never a public address.</summary>
    public static bool IsAllowedClaimOrigin(System.Net.IPAddress? address)
    {
        if (address is null) return false;
        if (System.Net.IPAddress.IsLoopback(address)) return true;
        var v4 = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (v4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var ip = v4.ToString();
        return IsPrivateLan(ip) || IsTailscaleIp(ip);
    }

    /// <summary>Physical adapter with a default gateway wins; Ethernet is preferred over Wi-Fi over other types.</summary>
    public static LanAdapterCandidate? SelectBest(IEnumerable<LanAdapterCandidate> candidates)
    {
        var eligible = candidates
            .Where(c => c.OperationalUp
                        && !string.IsNullOrWhiteSpace(c.Ipv4Address)
                        && !IsVirtualAdapter(c.Name, c.Description, c.Type)
                        && !IsApipa(c.Ipv4Address)
                        && IsPrivateLan(c.Ipv4Address))
            .ToList();
        if (eligible.Count == 0) return null;

        return eligible
            .OrderByDescending(c => c.HasDefaultGateway)
            .ThenBy(TypePriority)
            .First();

        static int TypePriority(LanAdapterCandidate c) => c.Type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => 0,
            NetworkInterfaceType.Wireless80211 => 1,
            _ => 2
        };
    }

    public static string ComputeBroadcast(string ipv4, string subnetMaskDotted)
    {
        var ip = ToUInt32(ipv4);
        var mask = ToUInt32(subnetMaskDotted);
        return FromUInt32(ip | ~mask);
    }

    public static string ComputeBroadcastFromPrefix(string ipv4, int prefixLength)
    {
        if (prefixLength is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        var ip = ToUInt32(ipv4);
        var mask = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
        return FromUInt32(ip | ~mask);
    }

    public static int MaskToPrefixLength(string subnetMaskDotted)
    {
        var mask = ToUInt32(subnetMaskDotted);
        var count = 0;
        for (var i = 31; i >= 0; i--)
        {
            if ((mask & (1u << i)) == 0) break;
            count++;
        }
        return count;
    }

    private static (byte, byte, byte, byte)? TryParseOctets(string ipv4)
    {
        var parts = ipv4.Split('.');
        if (parts.Length != 4) return null;
        var values = new byte[4];
        for (var i = 0; i < 4; i++)
            if (!byte.TryParse(parts[i], out values[i])) return null;
        return (values[0], values[1], values[2], values[3]);
    }

    private static uint ToUInt32(string ipv4)
    {
        var octets = TryParseOctets(ipv4) ?? throw new FormatException($"'{ipv4}' is not a valid IPv4 address.");
        var (a, b, c, d) = octets;
        return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
    }

    private static string FromUInt32(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
}

public interface INetworkInfoProvider
{
    NetworkSnapshot GetSnapshot(string? manualAdapterId);
}
