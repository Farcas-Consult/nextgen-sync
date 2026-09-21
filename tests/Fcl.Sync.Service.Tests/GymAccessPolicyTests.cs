using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.GymMaster;

namespace Fcl.Sync.Service.Tests;

public sealed class GymAccessPolicyTests
{
    private readonly GymAccessPolicy policy = new();

    [Theory]
    [InlineData("Current", "F", "LadiesAndStudio")]
    [InlineData("current", "M", "Mens")]
    [InlineData("Current", null, "Mens")]
    public void CurrentPaidMemberReceivesExpectedEntitlement(string status, string? gender, string entitlement)
    {
        var result = policy.Decide(Member(status, gender, 0));

        Assert.False(result.IsDisabled);
        Assert.Equal(entitlement, result.Entitlement);
    }

    [Theory]
    [InlineData("Expired", 0)]
    [InlineData("Current", 0.01)]
    [InlineData(null, 0)]
    public void InvalidMembershipIsDisabled(string? status, decimal owing)
    {
        var result = policy.Decide(Member(status, "F", owing));

        Assert.True(result.IsDisabled);
        Assert.Equal("NoAccess", result.Entitlement);
    }

    private static GymMasterMember Member(string? status, string? gender, decimal owing) => new()
    {
        MemberId = 1,
        Status = status,
        Gender = gender,
        Owing = owing
    };
}
