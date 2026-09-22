using System.Net;
using System.Text;
using System.Text.Json;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.ZKBio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Tests;

public sealed class ZKBioClientTests
{
    [Fact]
    public async Task UpsertSanitizesNamesRejectedAsSpecialCharacters()
    {
        string? upsertBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, "{\"code\":-1,\"message\":\"not found\"}");
            }

            upsertBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"code\":0,\"message\":\"success\"}");
        });
        var client = CreateClient(handler);
        var command = Command("991144") with
        {
            Name = "Anne-Marie O'Neil @ Gym!",
            LastName = "O'Neil"
        };

        var result = await client.ApplyPersonAsync(command, CancellationToken.None);

        Assert.Equal(AccessApplyOutcome.Applied, result.Outcome);
        using var body = JsonDocument.Parse(upsertBody!);
        Assert.Equal("Anne Marie O Neil Gym", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("O Neil", body.RootElement.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task NameContainingOnlySpecialCharactersFallsBackToPin()
    {
        string? upsertBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, "{\"code\":-1,\"message\":\"not found\"}");
            }

            upsertBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"code\":0}");
        });

        var result = await CreateClient(handler).ApplyPersonAsync(
            Command("991144") with { Name = "'@!" },
            CancellationToken.None);

        Assert.Equal(AccessApplyOutcome.Applied, result.Outcome);
        using var body = JsonDocument.Parse(upsertBody!);
        Assert.Equal("991144", body.RootElement.GetProperty("name").GetString());
    }

    private static ZKBioClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new ZKBioOptions
        {
            BaseUrl = "https://zkbio.test",
            AccessToken = "token"
        }),
        NullLogger<ZKBioClient>.Instance);

    private static AccessPersonCommand Command(string pin) => new()
    {
        Pin = pin,
        Name = "Test Member",
        Entitlement = "Mens",
        IsDisabled = false,
        AccessHash = "a",
        ProfileHash = "p",
        SyncHash = "s"
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
