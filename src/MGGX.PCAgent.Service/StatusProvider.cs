using System.Runtime.InteropServices;
using MGGX.PCAgent.Core;

namespace MGGX.PCAgent.Service;

public sealed class StatusProvider(IComponentProbe components, IPowerController power, TimeProvider clock) : BackgroundService, IStatusProvider
{
    public AgentStatus Current { get; private set; } = Empty();

    public async Task RefreshAsync(CancellationToken ct)
    {
        var sunshine = await components.GetSunshineAsync(ct);
        var tailscale = await components.GetTailscaleAsync(ct);
        Current = new(true, 1, AgentConstants.Version,
            new("online", Environment.MachineName, Environment.TickCount64 / 1000),
            new(RuntimeInformation.OSDescription, IsWorkstationLocked()), sunshine, tailscale,
            new(power.SleepSupported, power.HibernateSupported));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); } catch when (!stoppingToken.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken);
        }
    }

    private static AgentStatus Empty() => new(true, 1, AgentConstants.Version, new("online", Environment.MachineName, Environment.TickCount64 / 1000), new(RuntimeInformation.OSDescription, false), new(false, false), new(false, false), new(true, false));
    private static bool IsWorkstationLocked()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("LogonUI");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
