using System.Security.Cryptography;
using System.Text;

namespace MGGX.PCAgent.Core;

public sealed record PairingOffer(string Secret, string DisplayCode, DateTimeOffset ExpiresAtUtc, string Host, int Port);

public enum PairingClaimError { None, NoActiveOffer, Expired, AlreadyUsed, WrongSecret }

/// <summary>
/// Owned by the Windows Service, not the Control UI: the offer survives Control being closed and lives
/// until it is claimed, cancelled, regenerated, or it expires (10 minutes).
/// </summary>
public interface IPairingSession
{
    PairingOffer? Current { get; }
    PairingOffer GenerateOffer(string host, int port, TimeSpan? ttl = null);
    void Cancel();
    bool TryClaim(string secret, out PairingClaimError error);
}

public sealed class PairingSession(TimeProvider clock) : IPairingSession
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private PairingOffer? _offer;
    private bool _used;

    public PairingOffer? Current
    {
        get { lock (_lock) return IsExpired(_offer) ? null : _offer; }
    }

    /// <summary>Generating a new offer invalidates whatever offer was active before.</summary>
    public PairingOffer GenerateOffer(string host, int port, TimeSpan? ttl = null)
    {
        var offer = new PairingOffer(SecretGenerator.NewToken(), SecretGenerator.NewDisplayCode(), clock.GetUtcNow() + (ttl ?? DefaultTtl), host, port);
        lock (_lock) { _offer = offer; _used = false; }
        return offer;
    }

    public void Cancel()
    {
        lock (_lock) { _offer = null; _used = false; }
    }

    public bool TryClaim(string secret, out PairingClaimError error)
    {
        lock (_lock)
        {
            if (_offer is null) { error = PairingClaimError.NoActiveOffer; return false; }
            if (IsExpired(_offer)) { error = PairingClaimError.Expired; return false; }
            if (_used) { error = PairingClaimError.AlreadyUsed; return false; }
            if (!ConstantTimeEquals(secret, _offer.Secret)) { error = PairingClaimError.WrongSecret; return false; }

            _used = true;
            error = PairingClaimError.None;
            return true;
        }
    }

    private bool IsExpired(PairingOffer? offer) => offer is null || clock.GetUtcNow() >= offer.ExpiresAtUtc;

    private static bool ConstantTimeEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

public static class PairingQr
{
    public const string Scheme = "mggx://pc-agent/v1";

    public static string BuildUri(PairingOffer offer) =>
        $"{Scheme}?host={offer.Host}&port={offer.Port}&secret={offer.Secret}&expires={offer.ExpiresAtUtc.ToUnixTimeMilliseconds()}";
}

public sealed record PairClaimRequest(int ProtocolVersion, string? Secret, string? Client);

public sealed record PairClaimResponse(
    bool Ok,
    int ProtocolVersion,
    string AgentToken,
    int AgentPort,
    string AgentVersion,
    string PcId,
    string Name,
    string LanIp,
    string? TailscaleIp,
    string MacAddress,
    string BroadcastAddress);

public static class PairingConstants
{
    public const string ExpectedClient = "mggx-pc-control-home";
    public const int ProtocolVersion = 1;
}
