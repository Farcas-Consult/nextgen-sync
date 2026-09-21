using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.BioStar;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Tests;

public sealed class BioStarAccessProviderTests
{
    [Theory]
    [InlineData(3L, true)]
    [InlineData(2L, false)]
    [InlineData(null, false)]
    public void DefaultConfigurationOnlyHandlesCompanyThree(long? companyId, bool expected)
    {
        var provider = new BioStarAccessProvider(new UnusedClient(), Options.Create(new BioStarOptions()));

        Assert.Equal(expected, provider.HandlesCompany(companyId));
    }

    [Fact]
    public void DesiredStateHashChangesWhenProviderGroupsChange()
    {
        var command = new AccessPersonCommand
        {
            Pin = "42",
            Name = "Member",
            Entitlement = "Mens",
            IsDisabled = false,
            AccessHash = "access",
            ProfileHash = "profile",
            SyncHash = "sync"
        };
        var first = new BioStarAccessProvider(new UnusedClient(), Options.Create(new BioStarOptions
        {
            UserGroupId = "1052",
            AccessGroupId = "3"
        }));
        var second = new BioStarAccessProvider(new UnusedClient(), Options.Create(new BioStarOptions
        {
            UserGroupId = "1052",
            AccessGroupId = "4"
        }));

        Assert.NotEqual(first.GetDesiredStateHash(command), second.GetDesiredStateHash(command));
    }

    private sealed class UnusedClient : IBioStarClient
    {
        public Task<AccessApplyResult> ApplyPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyPeopleAsync(
            IReadOnlyList<AccessPersonCommand> commands, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
