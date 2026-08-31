namespace MGGX.PCAgent.Core;

/// <summary>
/// Local IPC contract between MGGX PC Agent Control (WinUI, unprivileged) and the pairing session that
/// lives in the Windows Service. Named Pipe only, never exposed over the network.
/// </summary>
public static class PairingIpc
{
    public const string PipeName = "MGGXPCAgentPairingPipe";
}

public sealed record PairingPipeRequest(string Command, string? CredentialId = null);

public sealed record PairingPipeOfferDto(string QrPayload, string DisplayCode, long ExpiresAtEpochMs, string Host, int Port);

public sealed record PairingPipeCredentialDto(string CredentialId, string Client, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastSeenUtc);

public sealed record PairingPipeResponse(bool Ok, string? Error = null, PairingPipeOfferDto? Offer = null, IReadOnlyList<PairingPipeCredentialDto>? Credentials = null)
{
    public static PairingPipeResponse Failure(string error) => new(false, error);
}
