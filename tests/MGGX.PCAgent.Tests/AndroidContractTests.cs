using System.Text.Json;
using MGGX.PCAgent.Core;
using Xunit;

namespace MGGX.PCAgent.Tests;

/// <summary>
/// Locks the wire contract against docs/PAIRING_API_V1.md and MGGX-PC-Control's
/// docs/PC_AGENT_PAIRING_V1.md (implemented by MGGX PC Control 2 alpha2's PcPairingProtocol.kt /
/// PairingClients.kt). Field names and casing here must never drift without updating both sides.
/// </summary>
public sealed class AndroidContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Qr_scheme_host_and_path_match_the_android_parser()
    {
        var offer = new PairingOffer("a".PadRight(43, 'a'), "123456", DateTimeOffset.UtcNow.AddMinutes(10), "192.168.1.20", 8766);
        var uri = new Uri(PairingQr.BuildUri(offer));
        Assert.Equal("mggx", uri.Scheme);
        Assert.Equal("pc-agent", uri.Host);
        Assert.Equal("/v1", uri.AbsolutePath);

        var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("192.168.1.20", query["host"]);
        Assert.Equal("8766", query["port"]);
        Assert.Equal(offer.Secret, query["secret"]);
        Assert.True(query.ContainsKey("expires"));
    }

    [Fact]
    public void Claim_request_deserializes_the_exact_field_names_android_sends()
    {
        const string json = """{"protocolVersion":1,"secret":"abc","client":"mggx-pc-control-home"}""";
        var request = JsonSerializer.Deserialize<PairClaimRequest>(json, WebJson);
        Assert.NotNull(request);
        Assert.Equal(1, request!.ProtocolVersion);
        Assert.Equal("abc", request.Secret);
        Assert.Equal("mggx-pc-control-home", request.Client);
    }

    [Fact]
    public void Claim_response_serializes_with_the_exact_field_names_android_requires()
    {
        var response = new PairClaimResponse(true, 1, "token-value", 8766, "1.1.0", "main", "MGGX PC",
            "192.168.1.20", "100.64.10.20", "00:11:22:33:44:55", "192.168.1.255");
        var json = JsonSerializer.Serialize(response, WebJson);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var required in new[] { "ok", "protocolVersion", "agentToken", "agentPort", "agentVersion", "pcId", "name", "lanIp", "tailscaleIp", "macAddress", "broadcastAddress" })
            Assert.True(root.TryGetProperty(required, out _), $"Missing required field '{required}'");

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("token-value", root.GetProperty("agentToken").GetString());
        Assert.Equal("192.168.1.20", root.GetProperty("lanIp").GetString());
        Assert.Equal("00:11:22:33:44:55", root.GetProperty("macAddress").GetString());
        Assert.Equal("192.168.1.255", root.GetProperty("broadcastAddress").GetString());
    }

    [Fact]
    public void Expected_client_and_protocol_version_constants_match_android()
    {
        Assert.Equal("mggx-pc-control-home", PairingConstants.ExpectedClient);
        Assert.Equal(1, PairingConstants.ProtocolVersion);
    }
}
