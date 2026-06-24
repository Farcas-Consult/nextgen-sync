namespace Fcl.Sync.Service.AccessControl;

public sealed record AccessDecision(
    string AccessLevelIds,
    bool IsDisabled,
    string DepartmentCode,
    string Reason);
