using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MGGX.PCAgent.Core;

namespace MGGX.PCAgent.Control;

/// <summary>Unprivileged client for the pairing administration channel hosted by the Windows Service.
/// No elevation is required: the pipe grants read/write to interactively logged-on users.</summary>
public static class PairingPipeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<PairingPipeResponse> SendAsync(PairingPipeRequest request, CancellationToken ct = default)
    {
        using var client = new NamedPipeClientStream(".", PairingIpc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await client.ConnectAsync(3000, ct); }
        catch (TimeoutException) { return PairingPipeResponse.Failure("service_unreachable"); }
        catch (IOException) { return PairingPipeResponse.Failure("service_unreachable"); }

        using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json));
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line)) return PairingPipeResponse.Failure("empty_response");
        return JsonSerializer.Deserialize<PairingPipeResponse>(line, Json) ?? PairingPipeResponse.Failure("invalid_response");
    }
}
