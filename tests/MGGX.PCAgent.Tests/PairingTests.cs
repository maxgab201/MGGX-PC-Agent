using System.Text.RegularExpressions;
using MGGX.PCAgent.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MGGX.PCAgent.Tests;

public sealed class PairingTests
{
    private static readonly Regex AndroidSecretPattern = new("^[A-Za-z0-9_-]{43}$");

    [Fact]
    public void Secret_is_256_bits_base64url_43_chars_no_padding()
    {
        var token = SecretGenerator.NewToken();
        Assert.Equal(43, token.Length);
        Assert.Matches(AndroidSecretPattern, token);
        Assert.DoesNotContain("=", token);
        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
    }

    [Fact]
    public void Secrets_are_unique_per_call()
    {
        var a = SecretGenerator.NewToken();
        var b = SecretGenerator.NewToken();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Display_code_is_always_six_digits()
    {
        for (var i = 0; i < 50; i++)
            Assert.Matches("^[0-9]{6}$", SecretGenerator.NewDisplayCode());
    }

    [Fact]
    public void Offer_expires_in_ten_minutes_by_default()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new PairingSession(clock);
        var offer = session.GenerateOffer("192.168.1.20", 8766);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(10), offer.ExpiresAtUtc);
    }

    [Fact]
    public void Valid_secret_within_ttl_claims_successfully()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new PairingSession(clock);
        var offer = session.GenerateOffer("192.168.1.20", 8766);
        Assert.True(session.TryClaim(offer.Secret, out var error));
        Assert.Equal(PairingClaimError.None, error);
    }

    [Fact]
    public void Expired_secret_is_rejected()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new PairingSession(clock);
        var offer = session.GenerateOffer("192.168.1.20", 8766);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.False(session.TryClaim(offer.Secret, out var error));
        Assert.Equal(PairingClaimError.Expired, error);
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new PairingSession(clock);
        session.GenerateOffer("192.168.1.20", 8766);
        Assert.False(session.TryClaim("wrong-secret-value-not-matching-offer-000000000", out var error));
        Assert.Equal(PairingClaimError.WrongSecret, error);
    }

    [Fact]
    public void No_offer_yet_is_rejected()
    {
        var session = new PairingSession(new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.False(session.TryClaim(SecretGenerator.NewToken(), out var error));
        Assert.Equal(PairingClaimError.NoActiveOffer, error);
    }

    [Fact]
    public void Secret_is_single_use()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new PairingSession(clock);
        var offer = session.GenerateOffer("192.168.1.20", 8766);
        Assert.True(session.TryClaim(offer.Secret, out _));
        Assert.False(session.TryClaim(offer.Secret, out var error));
        Assert.Equal(PairingClaimError.AlreadyUsed, error);
    }

    [Fact]
    public void Regenerating_invalidates_the_previous_secret()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new PairingSession(clock);
        var first = session.GenerateOffer("192.168.1.20", 8766);
        var second = session.GenerateOffer("192.168.1.20", 8766);
        Assert.NotEqual(first.Secret, second.Secret);
        Assert.False(session.TryClaim(first.Secret, out var error));
        Assert.Equal(PairingClaimError.WrongSecret, error);
        Assert.True(session.TryClaim(second.Secret, out _));
    }

    [Fact]
    public void Cancel_clears_the_current_offer()
    {
        var session = new PairingSession(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var offer = session.GenerateOffer("192.168.1.20", 8766);
        session.Cancel();
        Assert.Null(session.Current);
        Assert.False(session.TryClaim(offer.Secret, out var error));
        Assert.Equal(PairingClaimError.NoActiveOffer, error);
    }

    [Fact]
    public void Current_offer_is_null_once_expired()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var session = new PairingSession(clock);
        session.GenerateOffer("192.168.1.20", 8766);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(session.Current);
    }

    [Fact]
    public void Qr_uri_matches_the_android_contract()
    {
        var offer = new PairingOffer("s3cr3t", "482731", DateTimeOffset.FromUnixTimeMilliseconds(123456789), "192.168.1.20", 8766);
        var uri = PairingQr.BuildUri(offer);
        Assert.Equal("mggx://pc-agent/v1?host=192.168.1.20&port=8766&secret=s3cr3t&expires=123456789", uri);
    }
}
