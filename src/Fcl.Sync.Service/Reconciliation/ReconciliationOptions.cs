using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.Reconciliation;

public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    [Range(1, 24)]
    public int IntervalHours { get; init; } = 1;

    [Range(1, 120)]
    public int AccessProviderTimeoutMinutes { get; init; } = 20;
}
