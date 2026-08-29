namespace MGGX.PCAgent.Core;

public sealed record HealthResponse(bool Ok, string Service, int ApiVersion, string AgentVersion, long UptimeSeconds);
public sealed record PcStatus(string State, string MachineName, long UptimeSeconds);
public sealed record WindowsStatus(string Version, bool Locked);
public sealed record ComponentStatus(bool Installed, bool Running, string? Ip = null);
public sealed record PowerCapabilities(bool SleepSupported, bool HibernateSupported);
public sealed record AgentStatus(bool Ok, int ApiVersion, string AgentVersion, PcStatus Pc, WindowsStatus Windows,
    ComponentStatus Sunshine, ComponentStatus Tailscale, PowerCapabilities Power);
public sealed record ActionResponse(bool Ok, string State);
public sealed record ErrorResponse(bool Ok, string Error);

public sealed class AgentConfig
{
    public int Port { get; set; } = 8766;
    public bool DiscoveryEnabled { get; set; } = true;
    public int DiscoveryPort { get; set; } = 8767;
    public int LogRetentionDays { get; set; } = 7;
    public string[] AllowedNetworks { get; set; } = ["LocalSubnet", "100.64.0.0/10"];

    public bool IsValid() => Port is > 0 and <= 65535 && DiscoveryPort is > 0 and <= 65535 &&
        LogRetentionDays is >= 1 and <= 31 && AllowedNetworks.Length is > 0 and <= 16;
}

public static class AgentConstants
{
    public const string Version = "1.0.0";
    public const int ApiVersion = 1;
    public const string ServiceName = "MGGXPCAgent";
    public const string DisplayName = "MGGX PC Agent";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MGGX", "PC-Agent");
}
