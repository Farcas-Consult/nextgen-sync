namespace Fcl.Sync.Service.AccessControl;

public sealed record AccessDecision(
    string Entitlement,
    bool IsDisabled,
    string Reason);
