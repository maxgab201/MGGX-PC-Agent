using MGGX.PCAgent.Core;

namespace MGGX.PCAgent.Service;

public interface IPowerController
{
    bool SleepSupported { get; }
    bool HibernateSupported { get; }
    Task ShutdownAsync(CancellationToken cancellationToken);
    Task RestartAsync(CancellationToken cancellationToken);
    Task SleepAsync(CancellationToken cancellationToken);
    Task HibernateAsync(CancellationToken cancellationToken);
    Task LockAsync(CancellationToken cancellationToken);
}

public interface IComponentProbe
{
    Task<ComponentStatus> GetSunshineAsync(CancellationToken cancellationToken);
    Task<ComponentStatus> GetTailscaleAsync(CancellationToken cancellationToken);
    Task<bool> RestartSunshineAsync(CancellationToken cancellationToken);
}

public interface IStatusProvider { AgentStatus Current { get; } Task RefreshAsync(CancellationToken cancellationToken); }
