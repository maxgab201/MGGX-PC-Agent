using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MGGX.PCAgent.Core;
using MGGX.PCAgent.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MGGX.PCAgent.IntegrationTests;

public sealed class ApiTests : IClassFixture<AgentFactory>
{
    private readonly AgentFactory _factory;
    private readonly HttpClient _client;
    public ApiTests(AgentFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact] public async Task Health_returns_200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mggx-pc-agent", await response.Content.ReadAsStringAsync());
    }

    [Fact] public async Task Status_without_token_returns_401() => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/status")).StatusCode);

    [Fact] public async Task Status_with_valid_legacy_token_returns_200()
    {
        Authenticate(_client, AgentFactory.Token); var response = await _client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory] [InlineData("shutdown", "shutting_down")] [InlineData("restart", "restarting")] [InlineData("lock", "locking")]
    public async Task Confirmed_power_actions_return_200_after_verification(string action, string state)
    {
        Authenticate(_client, AgentFactory.Token); var response = await _client.PostAsync($"/api/v1/power/{action}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains(state, await response.Content.ReadAsStringAsync());
    }

    [Fact] public async Task Sleep_is_accepted_when_supported()
    {
        Authenticate(_client, AgentFactory.Token);
        var response = await _client.PostAsync("/api/v1/power/sleep", null);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted);
        Assert.Contains("sleeping", await response.Content.ReadAsStringAsync());
    }

    [Fact] public async Task Hibernate_unavailable_returns_409()
    {
        Authenticate(_client, AgentFactory.Token); Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/v1/power/hibernate", null)).StatusCode);
    }

    [Fact] public async Task Sunshine_restart_missing_returns_409()
    {
        Authenticate(_client, AgentFactory.Token); Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/v1/services/sunshine/restart", null)).StatusCode);
    }

    [Fact] public async Task Rate_limiting_returns_429()
    {
        Authenticate(_client, AgentFactory.Token);
        _client.DefaultRequestHeaders.Add("X-Test-Client-Id", Guid.NewGuid().ToString("N"));
        try
        {
            HttpResponseMessage? last = null;
            for (var i = 0; i < 35; i++) last = await _client.GetAsync("/api/v1/status");
            Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        }
        finally { _client.DefaultRequestHeaders.Remove("X-Test-Client-Id"); }
    }

    [Fact] public async Task Rate_limiting_is_partitioned_by_client()
    {
        Authenticate(_client, AgentFactory.Token);
        var clientId1 = "partition-test-client-1";
        var clientId2 = "partition-test-client-2";

        _client.DefaultRequestHeaders.Add("X-Test-Client-Id", clientId1);
        HttpResponseMessage? last = null;
        for (var i = 0; i < 35; i++) last = await _client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);

        _client.DefaultRequestHeaders.Remove("X-Test-Client-Id");
        _client.DefaultRequestHeaders.Add("X-Test-Client-Id", clientId2);
        var response2 = await _client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        _client.DefaultRequestHeaders.Remove("X-Test-Client-Id");
    }

    [Fact] public async Task Claim_with_valid_offer_returns_token_that_authenticates_immediately()
    {
        using var client = NewClient("claim-happy-path");
        var secret = GenerateOffer();

        var claimResponse = await client.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, secret, "mggx-pc-control-home"));
        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
        var claim = await claimResponse.Content.ReadFromJsonAsync<PairClaimResponse>();
        Assert.NotNull(claim);
        Assert.True(claim!.Ok);
        Assert.False(string.IsNullOrWhiteSpace(claim.AgentToken));
        Assert.False(string.IsNullOrWhiteSpace(claim.LanIp));
        Assert.False(string.IsNullOrWhiteSpace(claim.MacAddress));
        Assert.False(string.IsNullOrWhiteSpace(claim.BroadcastAddress));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claim.AgentToken);
        var statusResponse = await client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
    }

    [Fact] public async Task Claim_rejects_a_reused_secret()
    {
        using var client = NewClient("claim-reuse");
        var secret = GenerateOffer();

        var first = await client.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, secret, "mggx-pc-control-home"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, secret, "mggx-pc-control-home"));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact] public async Task Claim_rejects_wrong_secret()
    {
        using var client = NewClient("claim-wrong-secret");
        GenerateOffer();

        var response = await client.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, new string('z', 43), "mggx-pc-control-home"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact] public async Task Claim_rejects_wrong_protocol_version_or_client()
    {
        using var client = NewClient("claim-bad-request");
        var secret = GenerateOffer();

        var badProtocol = await client.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(2, secret, "mggx-pc-control-home"));
        Assert.Equal(HttpStatusCode.BadRequest, badProtocol.StatusCode);

        var badClient = await client.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, secret, "not-the-android-app"));
        Assert.Equal(HttpStatusCode.BadRequest, badClient.StatusCode);
    }

    [Fact] public async Task Claim_rate_limits_after_ten_attempts_per_minute()
    {
        using var client = NewClient("claim-rate-limit");
        HttpResponseMessage? last = null;
        for (var i = 0; i < 11; i++)
            last = await client.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, new string('y', 43), "mggx-pc-control-home"));
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact] public async Task Two_paired_devices_get_independent_tokens_and_revocation_only_affects_one()
    {
        using var clientA = NewClient("multi-token-a");
        var claimA = await (await clientA.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, GenerateOffer(), "mggx-pc-control-home")))
            .Content.ReadFromJsonAsync<PairClaimResponse>();

        using var clientB = NewClient("multi-token-b");
        var claimB = await (await clientB.PostAsJsonAsync("/api/v1/pair/claim", new PairClaimRequest(1, GenerateOffer(), "mggx-pc-control-home")))
            .Content.ReadFromJsonAsync<PairClaimResponse>();

        Assert.NotEqual(claimA!.AgentToken, claimB!.AgentToken);

        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claimA.AgentToken);
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claimB.AgentToken);
        Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync("/api/v1/status")).StatusCode);

        var credentials = _factory.Services.GetRequiredService<IPairedCredentialStore>();
        Assert.True(credentials.TryValidate(claimA.AgentToken, out var infoA));
        Assert.True(credentials.Revoke(infoA!.CredentialId));

        Assert.Equal(HttpStatusCode.Unauthorized, (await clientA.GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync("/api/v1/status")).StatusCode);
    }

    private HttpClient NewClient(string testId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Client-Id", testId);
        return client;
    }

    private string GenerateOffer() =>
        _factory.Services.GetRequiredService<IPairingSession>().GenerateOffer("192.168.1.20", 8766).Secret;

    private static void Authenticate(HttpClient client, string token) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

public sealed class AgentFactory : WebApplicationFactory<Program>
{
    public const string Token = "integration-test-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITokenStore>(); services.AddSingleton<ITokenStore>(new FakeTokenStore());
            services.RemoveAll<IPowerController>(); services.AddSingleton<IPowerController>(new FakePower());
            services.RemoveAll<IComponentProbe>(); services.AddSingleton<IComponentProbe>(new FakeComponents());
            services.RemoveAll<IStatusProvider>(); services.AddSingleton<IStatusProvider>(new FakeStatus());
            services.RemoveAll<INetworkInfoProvider>(); services.AddSingleton<INetworkInfoProvider>(new FakeNetwork());
            services.RemoveAll<IPairedCredentialStore>(); services.AddSingleton<IPairedCredentialStore>(new InMemoryCredentialStore());
            services.RemoveAll<IPairingSession>(); services.AddSingleton<IPairingSession>(new PairingSession(TimeProvider.System));

            // Named pipes and UDP discovery are platform/environment concerns unrelated to the HTTP
            // contract under test here; skip hosting them so the in-memory TestServer stays fast and quiet.
            foreach (var hostedService in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                         (d.ImplementationType == typeof(DiscoveryService) || d.ImplementationType == typeof(PairingPipeServer))).ToList())
                services.Remove(hostedService);
        });
    }

    private sealed class FakeTokenStore : ITokenStore { public string GetOrCreate() => Token; }
    private sealed class FakePower : IPowerController
    {
        public bool SleepSupported => true; public bool HibernateSupported => false;
        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask; public Task RestartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SleepAsync(CancellationToken ct) => Task.CompletedTask; public Task HibernateAsync(CancellationToken ct) => Task.CompletedTask; public Task LockAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class FakeComponents : IComponentProbe
    {
        public Task<ComponentStatus> GetSunshineAsync(CancellationToken ct) => Task.FromResult(new ComponentStatus(false, false));
        public Task<ComponentStatus> GetTailscaleAsync(CancellationToken ct) => Task.FromResult(new ComponentStatus(false, false));
        public Task<bool> RestartSunshineAsync(CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class FakeStatus : IStatusProvider
    {
        public AgentStatus Current { get; } = new(true, 1, "1.1.0", new("online", "TEST-PC", 1), new("Windows", false), new(false, false), new(false, false), new(true, false), "192.168.1.20");
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class FakeNetwork : INetworkInfoProvider
    {
        public NetworkSnapshot GetSnapshot(string? manualAdapterId) =>
            new("192.168.1.20", "192.168.1.255", "AA:BB:CC:DD:EE:FF", "Ethernet", "100.64.10.20", []);
    }
    private sealed class InMemoryCredentialStore : IPairedCredentialStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PairedCredentialInfo> _byToken = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tokenById = new();

        public (string CredentialId, string Token) Issue(string client, string name)
        {
            var token = SecretGenerator.NewToken();
            var id = Guid.NewGuid().ToString("N");
            _byToken[token] = new PairedCredentialInfo(id, client, name, DateTimeOffset.UtcNow, null);
            _tokenById[id] = token;
            return (id, token);
        }

        public bool TryValidate(string? suppliedToken, out PairedCredentialInfo? credential)
        {
            credential = null;
            if (string.IsNullOrWhiteSpace(suppliedToken)) return false;
            if (!_byToken.TryGetValue(suppliedToken, out var info)) return false;
            credential = info; return true;
        }

        public IReadOnlyList<PairedCredentialInfo> List() => _byToken.Values.ToList();

        public bool Revoke(string credentialId)
        {
            if (!_tokenById.TryRemove(credentialId, out var token)) return false;
            return _byToken.TryRemove(token, out _);
        }
    }
}
