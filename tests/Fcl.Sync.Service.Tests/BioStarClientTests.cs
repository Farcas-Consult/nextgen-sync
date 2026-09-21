using System.Net;
using System.Text;
using System.Text.Json;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.BioStar;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Tests;

public sealed class BioStarClientTests
{
    [Fact]
    public async Task Http200WithApplicationErrorIsFailure()
    {
        var writeCount = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login") return LoginResponse();
            if (request.Method != HttpMethod.Get) writeCount++;
            return Json(HttpStatusCode.OK,
                "{\"Response\":{\"code\":\"131136\",\"message\":\"Unknown exception\"}}");
        });

        var result = await CreateClient(handler).ApplyPersonAsync(Command("42"), CancellationToken.None);

        Assert.Equal(AccessApplyOutcome.Failed, result.Outcome);
        Assert.Contains("application code 131136", result.Message);
        Assert.Equal(0, writeCount);
    }

    [Fact]
    public async Task GroupMismatchUpdatesUserGroupAccessGroupAndExpiry()
    {
        CapturedRequest? update = null;
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login") return LoginResponse();
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, """
                    {"User":{"disabled":false,"user_group_id":{"id":"999"},
                    "access_groups":[{"id":"8"}],"expiry_datetime":"2029-01-01T00:00:00Z"}}
                    """);
            }
            update = await CapturedRequest.From(request);
            return Json(HttpStatusCode.OK, "{}");
        });

        var result = await CreateClient(handler).ApplyPersonAsync(Command("42"), CancellationToken.None);

        Assert.Equal(AccessApplyOutcome.Applied, result.Outcome);
        Assert.NotNull(update);
        Assert.Equal("PUT", update.Method);
        using var body = JsonDocument.Parse(update.Body!);
        var user = body.RootElement.GetProperty("User");
        Assert.Equal("1052", user.GetProperty("user_group_id").GetProperty("id").GetString());
        Assert.Equal("3", user.GetProperty("access_groups")[0].GetProperty("id").GetString());
        Assert.Equal("2030-12-31T23:59:00.00Z", user.GetProperty("expiry_datetime").GetString());
    }

    [Fact]
    public async Task BatchCancellationReturnsCompletedResultAndMarksUntouchedSuffixFailed()
    {
        using var cancellation = new CancellationTokenSource();
        var getCount = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login") return LoginResponse();
            getCount++;
            if (getCount == 2)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return Json(HttpStatusCode.OK, """
                {"User":{"disabled":false,"user_group_id":{"id":"1052"},
                "access_groups":[{"id":"3"}],"expiry_datetime":"2030-12-31T23:59:00Z"}}
                """);
        });
        var commands = new[] { Command("1"), Command("2"), Command("3") };

        var results = await CreateClient(handler).ApplyPeopleAsync(commands, cancellation.Token);

        Assert.Equal(AccessApplyOutcome.Skipped, results["1"].Outcome);
        Assert.Equal(AccessApplyOutcome.Failed, results["2"].Outcome);
        Assert.Equal(AccessApplyOutcome.Failed, results["3"].Outcome);
        Assert.Contains("cancelled", results["2"].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingUserCode201CreatesUserWithRequiredUppercaseWrapper()
    {
        var requests = new List<CapturedRequest>();
        var handler = new StubHandler(async request =>
        {
            requests.Add(await CapturedRequest.From(request));
            if (request.RequestUri!.AbsolutePath == "/api/login")
            {
                return LoginResponse();
            }

            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.BadRequest,
                    "{\"Response\":{\"code\":\"201\",\"message\":\"User can not be found with id\"}}");
            }

            return Json(HttpStatusCode.OK, "{}");
        });
        var client = CreateClient(handler);

        var result = await client.ApplyPersonAsync(Command("292328"), CancellationToken.None);

        Assert.Equal(AccessApplyOutcome.Applied, result.Outcome);
        var login = Assert.Single(requests, item => item.Path == "/api/login");
        var create = Assert.Single(requests, item => item.Method == "POST" && item.Path == "/api/users");
        using var loginJson = JsonDocument.Parse(login.Body!);
        Assert.True(loginJson.RootElement.TryGetProperty("User", out _));
        Assert.False(loginJson.RootElement.TryGetProperty("user", out _));
        using var createJson = JsonDocument.Parse(create.Body!);
        var user = createJson.RootElement.GetProperty("User");
        Assert.Equal(JsonValueKind.Number, user.GetProperty("user_id").ValueKind);
        Assert.Equal(292328, user.GetProperty("user_id").GetInt64());
        Assert.Equal("1052", user.GetProperty("user_group_id").GetProperty("id").GetString());
        Assert.Equal("3", user.GetProperty("access_groups")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task UnrelatedBadRequestDoesNotAttemptCreate()
    {
        var createCount = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login")
            {
                return LoginResponse();
            }
            if (request.Method == HttpMethod.Post) createCount++;
            return Json(HttpStatusCode.BadRequest, "{\"Response\":{\"code\":\"999\",\"message\":\"Bad request\"}}");
        });
        var client = CreateClient(handler);

        var result = await client.ApplyPersonAsync(Command("42"), CancellationToken.None);

        Assert.Equal(AccessApplyOutcome.Failed, result.Outcome);
        Assert.Equal(0, createCount);
    }

    private static BioStarClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new BioStarOptions
        {
            BaseUrl = "https://biostar.test",
            LoginId = "admin",
            Password = "secret",
            UserGroupId = "1052",
            AccessGroupId = "3"
        }),
        NullLogger<BioStarClient>.Instance);

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

    private static HttpResponseMessage LoginResponse()
    {
        var response = Json(HttpStatusCode.OK, "{}");
        response.Headers.Add("bs-session-id", "session");
        return response;
    }

    private sealed record CapturedRequest(string Method, string Path, string? Body)
    {
        public static async Task<CapturedRequest> From(HttpRequestMessage request) => new(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            request.Content is null ? null : await request.Content.ReadAsStringAsync());
    }
}
