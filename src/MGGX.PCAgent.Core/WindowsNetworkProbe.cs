using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MGGX.PCAgent.Core;

/// <summary>
/// Reads real Windows network adapters and picks the physical LAN adapter (Ethernet/Wi-Fi with a default
/// gateway) instead of virtual adapters created by WSL, Hyper-V, VMware, VirtualBox, Docker or Tailscale.
/// </summary>
public sealed class WindowsNetworkProbe : INetworkInfoProvider
{
    public NetworkSnapshot GetSnapshot(string? manualAdapterId)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToList();

        var candidates = new List<LanAdapterCandidate>();
        string? tailscaleIp = null;

        foreach (var nic in nics)
        {
            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
            var mac = FormatMac(nic.GetPhysicalAddress());

            foreach (var unicast in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var ip = unicast.Address.ToString();
                if (NetworkSelector.IsTailscaleIp(ip)) { tailscaleIp = ip; continue; }

                string? mask = null;
                try { mask = unicast.IPv4Mask?.ToString(); } catch (NotImplementedException) { }

                candidates.Add(new LanAdapterCandidate(nic.Id, nic.Name, nic.Description, nic.NetworkInterfaceType, true, hasGateway, ip, mask, mac));
            }
        }

        var available = candidates
            .Where(c => !NetworkSelector.IsVirtualAdapter(c.Name, c.Description, c.Type) && !NetworkSelector.IsApipa(c.Ipv4Address))
            .Select(c => new NetworkAdapterOption(c.Id, c.Name, c.Ipv4Address))
            .DistinctBy(o => o.Id)
            .ToList();

        var selected = manualAdapterId is not null
            ? candidates.FirstOrDefault(c => c.Id == manualAdapterId)
            : NetworkSelector.SelectBest(candidates);

        var broadcast = selected is { SubnetMask: not null }
            ? NetworkSelector.ComputeBroadcast(selected.Ipv4Address, selected.SubnetMask)
            : null;

        return new NetworkSnapshot(selected?.Ipv4Address, broadcast, selected?.MacAddress, selected?.Name, tailscaleIp, available);
    }

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? string.Empty : string.Join(':', bytes.Select(b => b.ToString("X2")));
    }
}
