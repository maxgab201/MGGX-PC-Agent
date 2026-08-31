using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MGGX.PCAgent.Core;

public sealed record PairedCredentialInfo(string CredentialId, string Client, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastSeenUtc);

/// <summary>
/// Stores one distinct 256-bit token per paired device (home phone). Only the SHA-256 hash and non-secret
/// metadata are ever persisted; the plaintext token is returned once, at issuance, and never again.
/// The legacy single Agent Token (<see cref="ITokenStore"/>) keeps working independently for compatibility.
/// </summary>
public interface IPairedCredentialStore
{
    (string CredentialId, string Token) Issue(string client, string name);
    bool TryValidate(string? suppliedToken, out PairedCredentialInfo? credential);
    IReadOnlyList<PairedCredentialInfo> List();
    bool Revoke(string credentialId);
}

public sealed class DpapiPairedCredentialStore : IPairedCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MGGX.PCAgent.PairedCredentials.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _path;
    private readonly object _lock = new();
    private List<StoredCredential> _credentials;

    public DpapiPairedCredentialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "paired-credentials.bin");
        _credentials = Load();
    }

    public (string CredentialId, string Token) Issue(string client, string name)
    {
        var token = SecretGenerator.NewToken();
        var credential = new StoredCredential(Guid.NewGuid().ToString("N"), client, name, Convert.ToBase64String(HashOf(token)), DateTimeOffset.UtcNow, null);
        lock (_lock)
        {
            _credentials = [.. _credentials, credential];
            Save(_credentials);
        }
        return (credential.CredentialId, token);
    }

    public bool TryValidate(string? suppliedToken, out PairedCredentialInfo? credential)
    {
        credential = null;
        if (string.IsNullOrWhiteSpace(suppliedToken)) return false;
        var suppliedHash = HashOf(suppliedToken.Trim());

        List<StoredCredential> snapshot;
        lock (_lock) snapshot = _credentials;

        StoredCredential? match = null;
        foreach (var candidate in snapshot)
        {
            if (CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(candidate.HashBase64), suppliedHash))
                match = candidate;
        }
        if (match is null) return false;

        credential = new PairedCredentialInfo(match.CredentialId, match.Client, match.Name, match.CreatedAtUtc, match.LastSeenUtc);
        TouchLastSeen(match.CredentialId);
        return true;
    }

    public IReadOnlyList<PairedCredentialInfo> List()
    {
        lock (_lock)
            return _credentials.Select(c => new PairedCredentialInfo(c.CredentialId, c.Client, c.Name, c.CreatedAtUtc, c.LastSeenUtc)).ToList();
    }

    public bool Revoke(string credentialId)
    {
        lock (_lock)
        {
            var remaining = _credentials.Where(c => c.CredentialId != credentialId).ToList();
            if (remaining.Count == _credentials.Count) return false;
            _credentials = remaining;
            Save(_credentials);
            return true;
        }
    }

    /// <summary>Updates in-memory LastSeen on every call but only persists periodically, so authenticated
    /// requests never pay a DPAPI-encrypt-and-write cost on the hot path.</summary>
    private void TouchLastSeen(string credentialId)
    {
        lock (_lock)
        {
            var index = _credentials.FindIndex(c => c.CredentialId == credentialId);
            if (index < 0) return;
            var now = DateTimeOffset.UtcNow;
            var stale = _credentials[index].LastSeenUtc is not { } last || now - last > TimeSpan.FromMinutes(5);
            _credentials[index] = _credentials[index] with { LastSeenUtc = now };
            if (stale) Save(_credentials);
        }
    }

    private static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private List<StoredCredential> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine));
            return JsonSerializer.Deserialize<List<StoredCredential>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(List<StoredCredential> credentials)
    {
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(_path, protectedBytes);
    }

    private sealed record StoredCredential(string CredentialId, string Client, string Name, string HashBase64, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastSeenUtc);
}
