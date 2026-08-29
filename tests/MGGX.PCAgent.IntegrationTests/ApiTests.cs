using System.Net;
using System.Net.Http.Headers;
using MGGX.PCAgent.Core;
using MGGX.PCAgent.Service;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MGGX.PCAgent.IntegrationTests;

public sealed class ApiTests : IClassFixture<AgentFactory>
{
    private readonly HttpClient _client;
    public ApiTests(AgentFactory factory) => _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact] public async Task Health_returns_200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mggx-pc-agent", await response.Content.ReadAsStringAsync());
    }

    [Fact] public async Task Status_without_token_returns_401() => Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/status")).StatusCode);

    [Fact] public async Task Status_with_valid_token_returns_200()
    {
        Authenticate(); var response = await _client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory] [InlineData("shutdown", "shutting_down")] [InlineData("restart", "restarting")] [InlineData("sleep", "sleeping")] [InlineData("lock", "locking")]
    public async Task Power_actions_are_accepted_without_executing_windows_action(string action, string state)
    {
        Authenticate(); var response = await _client.PostAsync($"/api/v1/power/{action}", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.Contains(state, await response.Content.ReadAsStringAsync());
    }

    [Fact] public async Task Hibernate_unavailable_returns_409()
    {
        Authenticate(); Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/v1/power/hibernate", null)).StatusCode);
    }

    [Fact] public async Task Sunshine_restart_missing_returns_409()
    {
        Authenticate(); Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/v1/services/sunshine/restart", null)).StatusCode);
    }

    [Fact] public async Task Rate_limiting_returns_429()
    {
        using var factory = new AgentFactory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AgentFactory.Token);
        HttpResponseMessage? last = null;
        for (var i = 0; i < 35; i++) last = await client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    private void Authenticate() => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AgentFactory.Token);
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
        public AgentStatus Current { get; } = new(true, 1, "1.0.0", new("online", "TEST-PC", 1), new("Windows", false), new(false, false), new(false, false), new(true, false));
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
