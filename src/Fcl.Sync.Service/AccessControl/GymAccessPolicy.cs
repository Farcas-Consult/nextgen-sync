using Fcl.Sync.Service.GymMaster;

namespace Fcl.Sync.Service.AccessControl;

public sealed class GymAccessPolicy : IAccessPolicy
{
    public AccessDecision Decide(GymMasterMember member)
    {
        var hasValidMembership = member.HasValidOwing && member.Owing <= 0 && IsCurrent(member.Status);

        if (!hasValidMembership)
        {
            var reason = member.HasValidOwing
                ? "Member is not current or has an outstanding balance."
                : "Member balance is missing or invalid; access denied for safety.";
            return new AccessDecision("NoAccess", true, reason);
        }

        if (string.Equals(member.Gender, "F", StringComparison.OrdinalIgnoreCase))
        {
            return new AccessDecision("LadiesAndStudio", false, "Current female member.");
        }

        return new AccessDecision("Mens", false, "Current male or unspecified-gender member.");
    }

    private static bool IsCurrent(string? status)
    {
        return string.Equals(status, "Current", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "current", StringComparison.OrdinalIgnoreCase);
    }
}
