using System.Security.AccessControl;
using System.Security.Principal;
using MGGX.PCAgent.Core;
using MGGX.PCAgent.Service;
using Xunit;

namespace MGGX.PCAgent.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mggx-agent-tests", Guid.NewGuid().ToString("N"));

    [Fact] public void Valid_auth_is_accepted() => Assert.True(TokenComparer.IsValid("secret", "secret"));
    [Theory] [InlineData(null)] [InlineData("")] [InlineData("wrong")]
    public void Invalid_auth_is_rejected(string? supplied) => Assert.False(TokenComparer.IsValid(supplied, "secret"));

    [Fact]
    public void Malformed_config_restores_safe_defaults()
    {
        Directory.CreateDirectory(_temp); File.WriteAllText(Path.Combine(_temp, "config.json"), "{bad json");
        var warned = false; var config = AgentConfigLoader.Load(_temp, _ => warned = true);
        Assert.True(warned); Assert.Equal(8766, config.Port); Assert.True(config.DiscoveryEnabled);
    }

    [Fact]
    public void Token_generation_is_256_bits_and_persistent()
    {
        var store = new DpapiTokenStore(_temp); var first = store.GetOrCreate(); var second = store.GetOrCreate();
        Assert.Equal(first, second); Assert.True(first.Length >= 43); Assert.DoesNotContain("=", first);
    }

    [Fact]
    public void Token_storage_acl_is_restricted()
    {
        new DpapiTokenStore(_temp).GetOrCreate();
        var acl = new FileInfo(Path.Combine(_temp, "agent-token.bin")).GetAccessControl();
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.DoesNotContain(rules, r => r.AccessControlType == AccessControlType.Allow &&
            r.IdentityReference is SecurityIdentifier sid && sid.IsWellKnown(WellKnownSidType.WorldSid));
    }

    [Fact]
    public void Logging_sanitizes_bearer_tokens()
    {
        var result = LogSanitizer.Sanitize("Authorization: Bearer abc.DEF-123_more");
        Assert.Equal("Authorization: Bearer [REDACTED]", result);
    }

    [Theory] [InlineData(true, true)] [InlineData(false, false)]
    public async Task Status_reflects_component_running_states(bool sunshineRunning, bool tailscaleRunning)
    {
        var provider = new StatusProvider(new FakeComponents(sunshineRunning, tailscaleRunning), new FakePower(), TimeProvider.System);
        await provider.RefreshAsync(default);
        Assert.Equal(sunshineRunning, provider.Current.Sunshine.Running);
        Assert.Equal(tailscaleRunning, provider.Current.Tailscale.Running);
    }

    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }

    private sealed class FakeComponents(bool sunshine, bool tailscale) : IComponentProbe
    {
        public Task<ComponentStatus> GetSunshineAsync(CancellationToken ct) => Task.FromResult(new ComponentStatus(sunshine, sunshine));
        public Task<ComponentStatus> GetTailscaleAsync(CancellationToken ct) => Task.FromResult(new ComponentStatus(tailscale, tailscale, tailscale ? "100.64.0.1" : null));
        public Task<bool> RestartSunshineAsync(CancellationToken ct) => Task.FromResult(sunshine);
    }
    private sealed class FakePower : IPowerController
    {
        public bool SleepSupported => true; public bool HibernateSupported => false;
        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask; public Task RestartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SleepAsync(CancellationToken ct) => Task.CompletedTask; public Task HibernateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LockAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
