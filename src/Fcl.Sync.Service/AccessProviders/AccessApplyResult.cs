namespace Fcl.Sync.Service.AccessProviders;

public sealed record AccessApplyResult(AccessApplyOutcome Outcome, string? Message = null)
{
    public static AccessApplyResult Applied(string? message = null) => new(AccessApplyOutcome.Applied, message);

    public static AccessApplyResult Skipped(string? message = null) => new(AccessApplyOutcome.Skipped, message);

    public static AccessApplyResult Failed(string? message = null) => new(AccessApplyOutcome.Failed, message);
}

public enum AccessApplyOutcome
{
    Applied,
    Skipped,
    Failed
}
