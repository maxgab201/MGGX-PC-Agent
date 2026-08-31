using System.Net;
using System.Net.NetworkInformation;
using MGGX.PCAgent.Core;
using Xunit;

namespace MGGX.PCAgent.Tests;

public sealed class NetworkTests
{
    [Theory]
    [InlineData("192.168.1.20", 24, "192.168.1.255")]
    [InlineData("10.20.0.5", 16, "10.20.255.255")]
    [InlineData("192.168.1.20", 23, "192.168.1.255")]
    [InlineData("192.168.1.20", 20, "192.168.15.255")]
    public void Broadcast_is_computed_from_real_prefix_length(string ip, int prefix, string expected) =>
        Assert.Equal(expected, NetworkSelector.ComputeBroadcastFromPrefix(ip, prefix));

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("255.255.254.0", 23)]
    [InlineData("255.255.240.0", 20)]
    public void Mask_round_trips_to_prefix_length(string mask, int expectedPrefix) =>
        Assert.Equal(expectedPrefix, NetworkSelector.MaskToPrefixLength(mask));

    [Fact]
    public void Broadcast_from_dotted_mask_matches_prefix_calculation() =>
        Assert.Equal("192.168.1.255", NetworkSelector.ComputeBroadcast("192.168.1.20", "255.255.255.0"));

    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("Ethernet 2", "VMware Virtual Ethernet Adapter for VMnet8")]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("Tailscale", "Tailscale Tunnel")]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)")]
    public void Virtual_adapters_are_excluded_by_name_or_description(string name, string description) =>
        Assert.True(NetworkSelector.IsVirtualAdapter(name, description, NetworkInterfaceType.Ethernet));

    [Fact]
    public void Loopback_type_is_always_virtual() =>
        Assert.True(NetworkSelector.IsVirtualAdapter("Loopback", "Software Loopback Interface", NetworkInterfaceType.Loopback));

    [Fact]
    public void Real_ethernet_and_wifi_adapters_are_not_virtual()
    {
        Assert.False(NetworkSelector.IsVirtualAdapter("Ethernet", "Intel(R) Ethernet Connection", NetworkInterfaceType.Ethernet));
        Assert.False(NetworkSelector.IsVirtualAdapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211));
    }

    [Fact]
    public void Apipa_addresses_are_rejected() => Assert.True(NetworkSelector.IsApipa("169.254.1.5"));

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.5", false)]
    public void Tailscale_range_is_100_64_over_10(string ip, bool expected) => Assert.Equal(expected, NetworkSelector.IsTailscaleIp(ip));

    [Fact]
    public void Adapter_with_default_gateway_wins_over_one_without()
    {
        var withGateway = new LanAdapterCandidate("2", "Ethernet", "Realtek", NetworkInterfaceType.Ethernet, true, true, "192.168.1.20", "255.255.255.0", "AA:BB:CC:DD:EE:01");
        var withoutGateway = new LanAdapterCandidate("1", "Ethernet 2", "Secondary", NetworkInterfaceType.Ethernet, true, false, "192.168.5.20", "255.255.255.0", "AA:BB:CC:DD:EE:02");
        var best = NetworkSelector.SelectBest([withoutGateway, withGateway]);
        Assert.Equal("192.168.1.20", best?.Ipv4Address);
    }

    [Fact]
    public void Ethernet_is_preferred_over_wifi_when_both_have_a_gateway()
    {
        var wifi = new LanAdapterCandidate("1", "Wi-Fi", "Wireless", NetworkInterfaceType.Wireless80211, true, true, "192.168.1.30", "255.255.255.0", "AA:BB:CC:DD:EE:03");
        var ethernet = new LanAdapterCandidate("2", "Ethernet", "Wired", NetworkInterfaceType.Ethernet, true, true, "192.168.1.20", "255.255.255.0", "AA:BB:CC:DD:EE:01");
        var best = NetworkSelector.SelectBest([wifi, ethernet]);
        Assert.Equal("192.168.1.20", best?.Ipv4Address);
    }

    [Fact]
    public void Virtual_and_apipa_and_public_candidates_are_never_selected()
    {
        var wsl = new LanAdapterCandidate("1", "vEthernet (WSL)", "Hyper-V", NetworkInterfaceType.Ethernet, true, false, "172.28.128.1", "255.255.240.0", "AA:00:00:00:00:01");
        var apipa = new LanAdapterCandidate("2", "Ethernet", "Realtek", NetworkInterfaceType.Ethernet, true, false, "169.254.1.5", "255.255.0.0", "AA:00:00:00:00:02");
        Assert.Null(NetworkSelector.SelectBest([wsl, apipa]));
    }

    [Fact]
    public void No_eligible_adapter_returns_null() => Assert.Null(NetworkSelector.SelectBest([]));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("100.90.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.5", false)]
    public void Claim_origin_allows_only_loopback_lan_and_tailscale(string ip, bool expected) =>
        Assert.Equal(expected, NetworkSelector.IsAllowedClaimOrigin(IPAddress.Parse(ip)));

    [Fact]
    public void Claim_origin_rejects_null_address() => Assert.False(NetworkSelector.IsAllowedClaimOrigin(null));
}
