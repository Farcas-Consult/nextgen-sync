using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.ZKBio;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Tests;

public sealed class ZKBioAccessProviderTests
{
    [Theory]
    [InlineData(4L)]
    [InlineData(null)]
    public void EmptyCompanyConfigurationHandlesAllCompanies(long? companyId)
    {
        var provider = new ZKBioAccessProvider(
            new UnusedClient(),
            Options.Create(new ZKBioOptions()));

        Assert.True(provider.HandlesCompany(companyId));
    }

    [Fact]
    public void NullCompanyConfigurationHandlesAllCompanies()
    {
        var provider = new ZKBioAccessProvider(
            new UnusedClient(),
            Options.Create(new ZKBioOptions { CompanyIds = null }));

        Assert.True(provider.HandlesCompany(4));
    }

    [Theory]
    [InlineData(4L, true)]
    [InlineData(3L, false)]
    [InlineData(null, false)]
    public void HandlesOnlyConfiguredCompanies(long? companyId, bool expected)
    {
        var provider = new ZKBioAccessProvider(
            new UnusedClient(),
            Options.Create(new ZKBioOptions { CompanyIds = [4] }));

        Assert.Equal(expected, provider.HandlesCompany(companyId));
    }

    private sealed class UnusedClient : IZKBioClient
    {
        public Task<AccessApplyResult> ApplyPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyPeopleAsync(
            IReadOnlyList<AccessPersonCommand> commands,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
