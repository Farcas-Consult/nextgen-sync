using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.Persistence;

public sealed class HistoryRetentionOptions
{
    public const string SectionName = "HistoryRetention";

    public bool Enabled { get; init; } = true;

    [Range(1, 168)]
    public int CleanupIntervalHours { get; init; } = 24;

    [Range(0, 365)]
    public int AccessCommandRetentionDays { get; init; }

    public bool KeepLatestAccessCommandPerPin { get; init; } = true;

    [Range(1, 365)]
    public int FailedAccessCommandRetentionDays { get; init; } = 30;

    [Range(1, 100)]
    public int MaxFailedAccessCommandsPerPin { get; init; } = 5;

    [Range(1, 365)]
    public int WebhookRetentionDays { get; init; } = 30;

    [Range(1, 365)]
    public int SyncRunRetentionDays { get; init; } = 30;

    [Range(1, 365)]
    public int IntegrationErrorRetentionDays { get; init; } = 90;

    public bool VacuumAfterCleanup { get; init; } = true;
}
