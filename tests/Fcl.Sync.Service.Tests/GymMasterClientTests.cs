using System.Net;
using System.Text;
using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.GymMaster;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Tests;

public sealed class GymMasterClientTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-a-balance")]
    [InlineData("KES ???")]
    public async Task UnknownOwingValueFailsClosed(string owing)
    {
        var json = $$"""{"result":[{"id":42,"companyid":3,"status":"Current","owing":"{{owing}}"}]}""";
        var client = CreateClient(json);

        var member = Assert.Single(await client.GetCurrentMembersAsync(CancellationToken.None));

        Assert.False(member.HasValidOwing);
        Assert.True(new GymAccessPolicy().Decide(member).IsDisabled);
    }

    [Theory]
    [InlineData("KSh 1,234.50", 1234.50)]
    [InlineData("-12.25", -12.25)]
    [InlineData("0.00", 0)]
    public async Task ParsesKnownOwingFormats(string owing, decimal expected)
    {
        var json = $$"""{"result":[{"id":42,"companyid":3,"status":"Current","owing":"{{owing}}"}]}""";
        var client = CreateClient(json);

        var member = Assert.Single(await client.GetCurrentMembersAsync(CancellationToken.None));

        Assert.True(member.HasValidOwing);
        Assert.Equal(expected, member.Owing);
    }

    private static GymMasterClient CreateClient(string response) => new(
        new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, response))),
        Options.Create(new GymMasterOptions { PortalMembersUrl = "https://gym.test/members" }));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
