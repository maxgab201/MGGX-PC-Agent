using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MGGX.PCAgent.Core;

namespace MGGX.PCAgent.Service;

/// <summary>
/// Hosts the pairing-session administration channel for MGGX PC Agent Control. The pairing session belongs
/// to the Service, so Control can generate/cancel a QR offer and list/revoke paired devices without ever
/// holding that state itself, and it keeps working after Control is closed.
/// </summary>
public sealed class PairingPipeServer(IPairingSession pairing, INetworkInfoProvider network, IPairedCredentialStore credentials, AgentConfig config, ILogger<PairingPipeServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(PairingIpc.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, CreatePipeSecurity());
                await server.WaitForConnectionAsync(ct);
                await HandleAsync(server, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pairing administration channel request failed");
                // A platform that cannot host named pipes (e.g. non-Windows test runs) must not spin this
                // loop at full CPU; back off between retries instead of failing instantly forever.
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line)) return;

        PairingPipeResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<PairingPipeRequest>(line, Json);
            response = request is null ? PairingPipeResponse.Failure("invalid_request") : Dispatch(request);
        }
        catch (JsonException) { response = PairingPipeResponse.Failure("invalid_request"); }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json));
    }

    private PairingPipeResponse Dispatch(PairingPipeRequest request) => request.Command switch
    {
        "generate" => Generate(),
        "cancel" => Cancel(),
        "status" => Status(),
        "listCredentials" => new PairingPipeResponse(true, Credentials: ToDto(credentials.List())),
        "revoke" when !string.IsNullOrWhiteSpace(request.CredentialId) => Revoke(request.CredentialId),
        _ => PairingPipeResponse.Failure("unknown_command"),
    };

    private PairingPipeResponse Generate()
    {
        var snapshot = network.GetSnapshot(config.LanAdapterId);
        if (snapshot.LanIp is null) return PairingPipeResponse.Failure("no_lan_adapter");
        var offer = pairing.GenerateOffer(snapshot.LanIp, config.Port);
        logger.LogInformation("pairing_generated");
        return new PairingPipeResponse(true, Offer: ToDto(offer));
    }

    private PairingPipeResponse Cancel()
    {
        pairing.Cancel();
        return new PairingPipeResponse(true);
    }

    private PairingPipeResponse Status()
    {
        var offer = pairing.Current;
        return new PairingPipeResponse(true, Offer: offer is null ? null : ToDto(offer));
    }

    private PairingPipeResponse Revoke(string credentialId)
    {
        var revoked = credentials.Revoke(credentialId);
        if (revoked) logger.LogInformation("credential_revoked");
        return revoked ? new PairingPipeResponse(true) : PairingPipeResponse.Failure("not_found");
    }

    private static PairingPipeOfferDto ToDto(PairingOffer offer) =>
        new(PairingQr.BuildUri(offer), offer.DisplayCode, offer.ExpiresAtUtc.ToUnixTimeMilliseconds(), offer.Host, offer.Port);

    private static IReadOnlyList<PairingPipeCredentialDto> ToDto(IReadOnlyList<PairedCredentialInfo> list) =>
        [.. list.Select(c => new PairingPipeCredentialDto(c.CredentialId, c.Client, c.Name, c.CreatedAtUtc, c.LastSeenUtc))];

    /// <summary>Local machine only: interactive/authenticated Windows users may connect; no network access, no anonymous logon.</summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}
