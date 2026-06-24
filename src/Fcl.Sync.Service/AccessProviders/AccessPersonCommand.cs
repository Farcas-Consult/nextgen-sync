using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.GymMaster;

namespace Fcl.Sync.Service.AccessProviders;

public sealed record AccessPersonCommand
{
    public required string Pin { get; init; }
    public required string Name { get; init; }
    public string? LastName { get; init; }
    public required string AccessLevelIds { get; init; }
    public required string DepartmentCode { get; init; }
    public required bool IsDisabled { get; init; }
    public string? Email { get; init; }
    public string? MobilePhone { get; init; }
    public DateOnly? JoinDate { get; init; }

    public static AccessPersonCommand From(GymMasterMember member, AccessDecision decision)
    {
        var fullName = $"{member.FirstName} {member.Surname}".Trim();

        return new AccessPersonCommand
        {
            Pin = member.MemberId.ToString(),
            Name = string.IsNullOrWhiteSpace(fullName) ? member.MemberId.ToString() : fullName,
            LastName = member.Surname,
            AccessLevelIds = decision.AccessLevelIds,
            DepartmentCode = decision.DepartmentCode,
            IsDisabled = decision.IsDisabled,
            Email = member.Email,
            MobilePhone = member.MobilePhone,
            JoinDate = member.JoinDate
        };
    }
}
