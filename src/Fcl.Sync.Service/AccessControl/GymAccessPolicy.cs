using Fcl.Sync.Service.GymMaster;

namespace Fcl.Sync.Service.AccessControl;

public sealed class GymAccessPolicy : IAccessPolicy
{
    private const string MensAccessLevelId = "2c9e83828d5efdf0018d5efede2f0442";
    private const string LadiesAccessLevelId = "2c9e838298504c94019855f6048f6337";
    private const string StudioAccessLevelId = "2c9e8382985f525a019863ffe0e44e26";

    public AccessDecision Decide(GymMasterMember member)
    {
        var hasValidMembership = member.Owing <= 0 && string.Equals(member.Status, "Current", StringComparison.OrdinalIgnoreCase);

        if (!hasValidMembership)
        {
            return new AccessDecision("null", true, "1", "Member is not current or has an outstanding balance.");
        }

        if (string.Equals(member.Gender, "F", StringComparison.OrdinalIgnoreCase))
        {
            return new AccessDecision($"{LadiesAccessLevelId},{StudioAccessLevelId}", false, "7", "Current female member.");
        }

        return new AccessDecision(MensAccessLevelId, false, "1", "Current male or unspecified-gender member.");
    }
}
