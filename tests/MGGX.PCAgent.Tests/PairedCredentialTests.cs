using MGGX.PCAgent.Core;
using Xunit;

namespace MGGX.PCAgent.Tests;

/// <summary>DPAPI-backed, so these only run on Windows (CI). See CoreTests for the platform note.</summary>
public sealed class PairedCredentialTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mggx-agent-credential-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Issued_token_is_256_bits_and_validates()
    {
        var store = new DpapiPairedCredentialStore(_temp);
        var (_, token) = store.Issue("mggx-pc-control-home", "Celular de casa");
        Assert.Equal(43, token.Length);
        Assert.True(store.TryValidate(token, out var info));
        Assert.Equal("Celular de casa", info!.Name);
    }

    [Fact]
    public void Two_paired_devices_receive_distinct_tokens_that_both_validate()
    {
        var store = new DpapiPairedCredentialStore(_temp);
        var (idA, tokenA) = store.Issue("mggx-pc-control-home", "Celular A");
        var (idB, tokenB) = store.Issue("mggx-pc-control-home", "Celular B");
        Assert.NotEqual(idA, idB);
        Assert.NotEqual(tokenA, tokenB);
        Assert.True(store.TryValidate(tokenA, out _));
        Assert.True(store.TryValidate(tokenB, out _));
    }

    [Fact]
    public void Revoking_one_credential_does_not_affect_others()
    {
        var store = new DpapiPairedCredentialStore(_temp);
        var (idA, tokenA) = store.Issue("mggx-pc-control-home", "Celular A");
        var (_, tokenB) = store.Issue("mggx-pc-control-home", "Celular B");

        Assert.True(store.Revoke(idA));

        Assert.False(store.TryValidate(tokenA, out _));
        Assert.True(store.TryValidate(tokenB, out _));
    }

    [Fact]
    public void Revoking_unknown_id_returns_false()
    {
        var store = new DpapiPairedCredentialStore(_temp);
        Assert.False(store.Revoke("does-not-exist"));
    }

    [Fact]
    public void Invalid_or_missing_token_never_validates()
    {
        var store = new DpapiPairedCredentialStore(_temp);
        store.Issue("mggx-pc-control-home", "Celular A");
        Assert.False(store.TryValidate(null, out _));
        Assert.False(store.TryValidate("", out _));
        Assert.False(store.TryValidate("not-a-real-token", out _));
    }

    [Fact]
    public void Plaintext_token_is_never_persisted_to_disk()
    {
        var store = new DpapiPairedCredentialStore(_temp);
        var (_, token) = store.Issue("mggx-pc-control-home", "Celular A");
        var raw = File.ReadAllBytes(Path.Combine(_temp, "paired-credentials.bin"));
        // DPAPI-encrypted on disk; even so, the plaintext token must not appear anywhere in the bytes.
        var haystack = Convert.ToBase64String(raw);
        Assert.DoesNotContain(token, haystack);
    }

    [Fact]
    public void List_reflects_issued_and_revoked_credentials()
    {
        var store = new DpapiPairedCredentialStore(_temp);
        var (id, _) = store.Issue("mggx-pc-control-home", "Celular A");
        Assert.Single(store.List());
        store.Revoke(id);
        Assert.Empty(store.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
