using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;

namespace MGGX.PCAgent.Service;

public sealed class WindowsComponentProbe : IComponentProbe
{
    private static readonly string[] SunshineServices = ["SunshineService", "sunshine"];

    public Task<ComponentStatus> GetSunshineAsync(CancellationToken ct)
    {
        foreach (var name in SunshineServices)
        {
            try { using var sc = new ServiceController(name); return Task.FromResult(new ComponentStatus(true, sc.Status == ServiceControllerStatus.Running)); }
            catch (InvalidOperationException) { }
        }
        var running = Process.GetProcessesByName("sunshine").Length > 0;
        var installed = running || FindSunshineExecutable() is not null;
        return Task.FromResult(new ComponentStatus(installed, running));
    }

    public Task<ComponentStatus> GetTailscaleAsync(CancellationToken ct)
    {
        var installed = false; var running = false;
        try { using var sc = new ServiceController("Tailscale"); installed = true; running = sc.Status == ServiceControllerStatus.Running; }
        catch (InvalidOperationException) { }
        var ip = running ? FindTailscaleIp() : null;
        return Task.FromResult(new ComponentStatus(installed, running, ip));
    }

    public async Task<bool> RestartSunshineAsync(CancellationToken ct)
    {
        foreach (var name in SunshineServices)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); await WaitAsync(sc, ServiceControllerStatus.Stopped, ct); }
                sc.Start(); await WaitAsync(sc, ServiceControllerStatus.Running, ct); return true;
            }
            catch (InvalidOperationException) { }
        }
        var exe = FindSunshineExecutable();
        if (exe is null) return false;
        foreach (var p in Process.GetProcessesByName("sunshine")) { try { p.Kill(true); await p.WaitForExitAsync(ct); } finally { p.Dispose(); } }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return true;
    }

    private static async Task WaitAsync(ServiceController sc, ServiceControllerStatus status, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until) { ct.ThrowIfCancellationRequested(); sc.Refresh(); if (sc.Status == status) return; await Task.Delay(250, ct); }
        throw new TimeoutException("Service state change timed out.");
    }

    private static string? FindSunshineExecutable()
    {
        var paths = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine", "sunshine.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine", "sunshine.exe") };
        return paths.FirstOrDefault(File.Exists);
    }

    private static string? FindTailscaleIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(x => x.Address).Where(x => x.AddressFamily == AddressFamily.InterNetwork))
            { var b = address.GetAddressBytes(); if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return address.ToString(); }
        return null;
    }
}
