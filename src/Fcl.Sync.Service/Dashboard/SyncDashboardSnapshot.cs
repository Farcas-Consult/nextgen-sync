namespace Fcl.Sync.Service.Dashboard;

public sealed record SyncDashboardSnapshot(
    DateTimeOffset GeneratedAt,
    int MemberCount,
    int WebhookEventCount,
    int PendingCommandCount,
    int FailedCommandCount,
    SyncRunSummary? LatestSyncRun,
    IReadOnlyList<CommandStatusCount> LatestCommandCounts,
    IReadOnlyList<CommandStatusCount> AllCommandCounts,
    IReadOnlyList<SyncRunSummary> RecentSyncRuns,
    IReadOnlyList<IntegrationErrorSummary> RecentErrors);

public sealed record SyncRunSummary(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int MembersChecked,
    string Status,
    string? ErrorMessage)
{
    public TimeSpan Duration => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase)
        ? DateTimeOffset.UtcNow - StartedAt
        : (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

public sealed record CommandStatusCount(string Status, int Count);

public sealed record IntegrationErrorSummary(
    string Source,
    string Message,
    DateTimeOffset CreatedAt);
