using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fcl.Sync.Service.AccessProviders;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.BioStar;

public sealed class BioStarClient : IBioStarClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;
    private readonly BioStarOptions options;
    private readonly ILogger<BioStarClient> logger;
    private readonly SemaphoreSlim authenticationLock = new(1, 1);
    private string? sessionId;

    public BioStarClient(HttpClient httpClient, IOptions<BioStarOptions> options, ILogger<BioStarClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;

        if (string.IsNullOrWhiteSpace(this.options.BaseUrl) ||
            string.IsNullOrWhiteSpace(this.options.LoginId) ||
            string.IsNullOrWhiteSpace(this.options.Password))
        {
            throw new InvalidOperationException("BioStar provider requires BioStar:BaseUrl, BioStar:LoginId and BioStar:Password.");
        }

        this.httpClient.BaseAddress = new Uri(this.options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<AccessApplyResult> ApplyPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyCoreAsync(command, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "BioStar failed to apply PIN {Pin}.", command.Pin);
            return AccessApplyResult.Failed(ex.Message);
        }
    }

    public async Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyPeopleAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, AccessApplyResult>(StringComparer.Ordinal);

        // Authenticate once up front. A subsequently expired session is refreshed and retried by SendAsync.
        try
        {
            await EnsureAuthenticatedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "BioStar batch authentication failed.");
            foreach (var command in commands)
            {
                results[command.Pin] = AccessApplyResult.Failed(ex.Message);
            }
            return results;
        }

        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[command.Pin] = await ApplyPersonAsync(command, cancellationToken);
        }

        return results;
    }

    private async Task<AccessApplyResult> ApplyCoreAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        var current = await GetUserAsync(command.Pin, cancellationToken);
        if (current is null)
        {
            await CreateUserAsync(command, cancellationToken);
            return AccessApplyResult.Applied("Created BioStar user.");
        }

        if (current.Disabled == command.IsDisabled)
        {
            return AccessApplyResult.Skipped("BioStar user already has the desired status.");
        }

        await UpdateUserAsync(command, current.Shape, cancellationToken);
        return AccessApplyResult.Applied("Updated BioStar user status.");
    }

    private async Task<BioStarUserState?> GetUserAsync(string pin, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/users/{Uri.EscapeDataString(pin)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await ReadSuccessfulBodyAsync(response, $"get user {pin}", cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var shape = BioStarResponseShape.Direct;
        var user = root;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("User", out var oldUser))
        {
            shape = BioStarResponseShape.UppercaseUserWrapper;
            user = oldUser;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out var newUser))
        {
            shape = BioStarResponseShape.LowercaseUserWrapper;
            user = newUser;
        }

        var disabled = TryGetPropertyIgnoreCase(user, "disabled", out var value) && IsTrue(value);
        return new BioStarUserState(disabled, shape);
    }

    private async Task CreateUserAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        var name = SanitizeName(command.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"BioStar user {command.Pin} cannot be created without a name.");
        }

        var payload = new Dictionary<string, object>
        {
            ["User"] = new
            {
                user_id = command.Pin,
                name,
                email = string.IsNullOrWhiteSpace(command.Email) ? $"user{command.Pin}@gym.local" : command.Email.Trim(),
                start_datetime = FormatDate(options.StartDateTime),
                expiry_datetime = FormatDate(options.ExpiryDateTime),
                user_group_id = new { id = options.UserGroupId },
                disabled = command.IsDisabled,
                access_groups = new[] { new { id = options.AccessGroupId } }
            }
        };

        using var response = await SendAsync(HttpMethod.Post, "api/users", payload, cancellationToken);
        await ReadSuccessfulBodyAsync(response, $"create user {command.Pin}", cancellationToken);
        logger.LogInformation("Created BioStar user {Pin}.", command.Pin);
    }

    private async Task UpdateUserAsync(
        AccessPersonCommand command,
        BioStarResponseShape shape,
        CancellationToken cancellationToken)
    {
        object payload = shape == BioStarResponseShape.UppercaseUserWrapper
            ? new Dictionary<string, object>
            {
                ["User"] = new
                {
                    user_id = command.Pin,
                    disabled = command.IsDisabled,
                    access_groups = new[] { new { id = options.AccessGroupId } }
                }
            }
            : new
            {
                user = new { user_id = command.Pin, disabled = command.IsDisabled },
                access_groups = new[] { new { id = options.AccessGroupId } }
            };

        using var response = await SendAsync(
            HttpMethod.Put,
            $"api/users/{Uri.EscapeDataString(command.Pin)}",
            payload,
            cancellationToken);
        await ReadSuccessfulBodyAsync(response, $"update user {command.Pin}", cancellationToken);
        logger.LogInformation("Updated BioStar user {Pin}; disabled={Disabled}.", command.Pin, command.IsDisabled);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? payload,
        CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await SendOnceAsync(method, relativeUrl, payload, sessionId!, cancellationToken);

        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return response;
        }

        response.Dispose();
        sessionId = null;
        await EnsureAuthenticatedAsync(cancellationToken);
        return await SendOnceAsync(method, relativeUrl, payload, sessionId!, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string relativeUrl,
        object? payload,
        string session,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.TryAddWithoutValidation("bs-session-id", session);
        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        }
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await authenticationLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            var payload = new Dictionary<string, object>
            {
                ["User"] = new { login_id = options.LoginId, password = options.Password }
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync("api/login", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"BioStar authentication returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            if (!response.Headers.TryGetValues("bs-session-id", out var values) ||
                string.IsNullOrWhiteSpace(sessionId = values.FirstOrDefault()))
            {
                throw new InvalidOperationException("BioStar authentication did not return a bs-session-id header.");
            }

            logger.LogInformation("Authenticated with BioStar.");
        }
        finally
        {
            authenticationLock.Release();
        }
    }

    private static async Task<string> ReadSuccessfulBodyAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"BioStar {operation} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
        return body;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool IsTrue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
        _ => false
    };

    private static string SanitizeName(string value) => value.Replace("'", "", StringComparison.Ordinal)
        .Replace("`", "", StringComparison.Ordinal)
        .Trim();

    private static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.00'Z'");

    private sealed record BioStarUserState(bool Disabled, BioStarResponseShape Shape);

    private enum BioStarResponseShape
    {
        Direct,
        UppercaseUserWrapper,
        LowercaseUserWrapper
    }
}
