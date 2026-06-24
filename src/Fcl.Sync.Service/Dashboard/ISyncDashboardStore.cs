namespace Fcl.Sync.Service.Dashboard;

public interface ISyncDashboardStore
{
    Task<SyncDashboardSnapshot> GetDashboardSnapshotAsync(CancellationToken cancellationToken);
}
