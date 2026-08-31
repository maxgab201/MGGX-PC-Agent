using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MGGX.PCAgent.Core;

namespace MGGX.PCAgent.Service;

public sealed class DiscoveryService(AgentConfig config, ILogger<DiscoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.DiscoveryEnabled) return;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, config.DiscoveryPort));
        logger.LogInformation("LAN discovery listening on UDP {Port}", config.DiscoveryPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var request = await udp.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(request.Buffer).Trim() != "MGGX_DISCOVER_V1") continue;
                var response = JsonSerializer.SerializeToUtf8Bytes(new { service = "mggx-pc-agent", apiVersion = 1, port = config.Port, machineName = Environment.MachineName });
                await udp.SendAsync(response, request.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discovery request failed");
                // A persistently broken socket (e.g. no UDP support in a sandboxed environment) must not
                // spin the loop at full CPU; back off between retries instead.
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { }
            }
        }
    }
}
