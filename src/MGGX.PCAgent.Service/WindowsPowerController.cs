using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MGGX.PCAgent.Service;

public sealed class WindowsPowerController : IPowerController
{
    public bool SleepSupported => GetPwrCapabilities(out var caps) && caps.SystemS3;
    public bool HibernateSupported => GetPwrCapabilities(out var caps) && caps.HiberFilePresent;

    public Task ShutdownAsync(CancellationToken ct) => RunAsync("shutdown.exe", "/s /t 0", ct);
    public Task RestartAsync(CancellationToken ct) => RunAsync("shutdown.exe", "/r /t 0", ct);
    public Task SleepAsync(CancellationToken ct) => SuspendAsync(false);
    public Task HibernateAsync(CancellationToken ct) => SuspendAsync(true);

    public Task LockAsync(CancellationToken ct)
    {
        // tsdiscon disconnects the active console session and requires credentials to resume.
        return RunAsync(Path.Combine(Environment.SystemDirectory, "tsdiscon.exe"), "console", ct);
    }

    private static Task SuspendAsync(bool hibernate)
    {
        if (!SetSuspendState(hibernate, false, false)) throw new InvalidOperationException("Windows rejected the requested power transition.");
        return Task.CompletedTask;
    }

    private static async Task RunAsync(string file, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("Windows could not start the power action.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Power action returned exit code {process.ExitCode}.");
    }

    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SystemPowerCapabilities
    {
        [MarshalAs(UnmanagedType.U1)] public bool PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5, HiberFilePresent;
        [MarshalAs(UnmanagedType.U1)] public bool FullWake, VideoDimPresent, ApmPresent, UpsPresent, ThermalControl, ProcessorThrottle, ProcessorMinThrottle, ProcessorMaxThrottle, FastSystemS4, Hiberboot, WakeAlarmPresent, AoAc, DiskSpinDown;
        public byte HiberFileType;
        [MarshalAs(UnmanagedType.U1)] public bool AoAcConnectivitySupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Spare3;
        [MarshalAs(UnmanagedType.U1)] public bool SystemBatteriesPresent, BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public BatteryReportingScale[] BatteryScale;
        public SystemPowerState AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BatteryReportingScale { public uint Granularity, Capacity; }
    private enum SystemPowerState { Unspecified, Working, Sleeping1, Sleeping2, Sleeping3, Hibernate, Shutdown, Maximum }
}
