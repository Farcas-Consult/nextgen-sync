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

        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[command.Pin] = await ApplyPersonAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Return completed results so the caller can persist confirmed progress. Mark
                // the untouched suffix explicitly; it remains eligible for a future retry.
                for (var remaining = index; remaining < commands.Count; remaining++)
                {
                    results[commands[remaining].Pin] = AccessApplyResult.Failed(
                        "BioStar batch was cancelled before this member was processed.");
                }
                logger.LogWarning(
                    "BioStar batch was cancelled after completing {CompletedCount} of {TotalCount} members.",
                    index,
                    commands.Count);
                break;
            }
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

        if (current.Disabled == command.IsDisabled &&
            string.Equals(current.UserGroupId, options.UserGroupId, StringComparison.Ordinal) &&
            current.AccessGroupIds.Count == 1 &&
            current.AccessGroupIds.Contains(options.AccessGroupId, StringComparer.Ordinal) &&
            (current.ExpiryDateTime is null || DatesMatch(current.ExpiryDateTime.Value, options.ExpiryDateTime)))
        {
            return AccessApplyResult.Skipped("BioStar user already has the desired status and groups.");
        }

        await UpdateUserAsync(command, current.Shape, cancellationToken);
        return AccessApplyResult.Applied("Updated BioStar user status, groups, and validity.");
    }

    private async Task<BioStarUserState?> GetUserAsync(string pin, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/users/{Uri.EscapeDataString(pin)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest && IsUserNotFound(body))
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"BioStar get user {pin} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        EnsureSuccessfulApplicationResponse(body, $"get user {pin}");

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
        var userGroupId = ReadIdProperty(user, "user_group_id") ?? ReadIdProperty(root, "user_group_id");
        var accessGroupsContainer = user;
        if (!TryGetPropertyIgnoreCase(user, "access_groups", out _) &&
            TryGetPropertyIgnoreCase(root, "access_groups", out _))
        {
            accessGroupsContainer = root;
        }
        var accessGroupIds = ReadIdArray(accessGroupsContainer, "access_groups");
        DateTimeOffset? expiry = null;
        var hasExpiry = TryGetPropertyIgnoreCase(user, "expiry_datetime", out var expiryValue) ||
            TryGetPropertyIgnoreCase(root, "expiry_datetime", out expiryValue);
        if (hasExpiry &&
            expiryValue.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(expiryValue.GetString(), out var parsedExpiry))
        {
            expiry = parsedExpiry;
        }
        return new BioStarUserState(disabled, userGroupId, accessGroupIds, expiry, shape);
    }

    private static bool IsUserNotFound(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("Response", out var response))
            {
                return false;
            }

            var hasNotFoundCode = response.TryGetProperty("code", out var code) &&
                (code.ValueKind == JsonValueKind.String && code.GetString() == "201" ||
                 code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number == 201);
            var hasNotFoundMessage = response.TryGetProperty("message", out var message) &&
                message.GetString()?.Contains("can not be found", StringComparison.OrdinalIgnoreCase) == true;
            return hasNotFoundCode || hasNotFoundMessage;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task CreateUserAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        var name = SanitizeAndLimit(command.Name, options.MaxNameLength);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"BioStar user {command.Pin} cannot be created without a name.");
        }

        var userId = ToBioStarUserId(command.Pin);
        var email = SanitizeEmail(command.Email, command.Pin);

        var payload = new Dictionary<string, object>
        {
            ["User"] = new
            {
                user_id = userId,
                name,
                email,
                start_datetime = FormatDate(options.StartDateTime),
                expiry_datetime = FormatDate(options.ExpiryDateTime),
                user_group_id = new { id = options.UserGroupId },
                disabled = command.IsDisabled,
                access_groups = new[] { new { id = options.AccessGroupId } }
            }
        };

        using var response = await SendAsync(HttpMethod.Post, "api/users", payload, cancellationToken);
        await ReadSuccessfulBodyAsync(response, $"create user {command.Pin} ({Describe(command, name, email)})", cancellationToken);
        logger.LogInformation("Created BioStar user {Pin}.", command.Pin);
    }

    private async Task UpdateUserAsync(
        AccessPersonCommand command,
        BioStarResponseShape shape,
        CancellationToken cancellationToken)
    {
        var userId = ToBioStarUserId(command.Pin);
        object payload = shape == BioStarResponseShape.UppercaseUserWrapper
            ? new Dictionary<string, object>
            {
                ["User"] = new
                {
                    user_id = userId,
                    disabled = command.IsDisabled,
                    expiry_datetime = FormatDate(options.ExpiryDateTime),
                    user_group_id = new { id = options.UserGroupId },
                    access_groups = new[] { new { id = options.AccessGroupId } }
                }
            }
            : new
            {
                user = new
                {
                    user_id = userId,
                    disabled = command.IsDisabled,
                    expiry_datetime = FormatDate(options.ExpiryDateTime),
                    user_group_id = new { id = options.UserGroupId }
                },
                access_groups = new[] { new { id = options.AccessGroupId } }
            };

        using var response = await SendAsync(
            HttpMethod.Put,
            $"api/users/{Uri.EscapeDataString(command.Pin)}",
            payload,
            cancellationToken);
        await ReadSuccessfulBodyAsync(response, $"update user {command.Pin} ({Describe(command, null, null)})", cancellationToken);
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

        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) &&
            !await IsExpiredSessionResponseAsync(response, cancellationToken))
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

            EnsureSuccessfulApplicationResponse(body, "authentication");

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
        EnsureSuccessfulApplicationResponse(body, operation);
        return body;
    }

    private static void EnsureSuccessfulApplicationResponse(string body, string operation)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!TryGetPropertyIgnoreCase(document.RootElement, "Response", out var response) ||
                !TryGetPropertyIgnoreCase(response, "code", out var code)) return;

            var value = code.ValueKind == JsonValueKind.String ? code.GetString() : code.GetRawText();
            if (string.IsNullOrWhiteSpace(value) || value is "0" or "200") return;
            var message = TryGetPropertyIgnoreCase(response, "message", out var messageElement)
                ? messageElement.ToString() : "Unknown BioStar application error";
            throw new HttpRequestException($"BioStar {operation} returned application code {value}: {message}");
        }
        catch (JsonException)
        {
            // Some successful BioStar operations return an empty or non-JSON body.
        }
    }

    private static async Task<bool> IsExpiredSessionResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!TryGetPropertyIgnoreCase(document.RootElement, "Response", out var result)) return false;
            var code = TryGetPropertyIgnoreCase(result, "code", out var codeValue) ? codeValue.ToString() : "";
            var message = TryGetPropertyIgnoreCase(result, "message", out var value) ? value.ToString() : "";
            return code is "401" or "403" ||
                   (message.Contains("session", StringComparison.OrdinalIgnoreCase) &&
                    (message.Contains("expire", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("not found", StringComparison.OrdinalIgnoreCase))) ||
                   message.Contains("login required", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("not logged", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static string SanitizeAndLimit(string value, int maxLength)
    {
        var sanitized = new string(value.Where(c => !char.IsControl(c) && c is not '\'' and not '`').ToArray()).Trim();
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength].TrimEnd();
    }

    private string SanitizeEmail(string? value, string pin)
    {
        var fallback = $"user{pin}@gym.local";
        var candidate = string.IsNullOrWhiteSpace(value)
            ? fallback
            : new string(value.Trim().Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
        if (!candidate.Contains('@') || candidate.Length > options.MaxEmailLength)
        {
            candidate = fallback;
        }
        if (candidate.Length > options.MaxEmailLength)
        {
            throw new InvalidOperationException($"BioStar fallback email for PIN {pin} exceeds the configured maximum length.");
        }
        return candidate;
    }

    private object ToBioStarUserId(string pin) =>
        options.UseNumericUserIdWhenPossible && long.TryParse(pin, out var numericId) ? numericId : pin;

    private static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.00'Z'");

    private static string? ReadIdProperty(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(property, "id", out var id))
            return id.ToString();
        return property.ValueKind is JsonValueKind.String or JsonValueKind.Number ? property.ToString() : null;
    }

    private static IReadOnlyList<string> ReadIdArray(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(item, "id", out var id)
                    ? id.ToString() : item.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
    }

    private static bool DatesMatch(DateTimeOffset actual, DateTimeOffset desired) =>
        Math.Abs((actual.ToUniversalTime() - desired.ToUniversalTime()).TotalSeconds) < 1;

    private string Describe(AccessPersonCommand command, string? name, string? email) =>
        $"pin={command.Pin}, disabled={command.IsDisabled}, userGroup={options.UserGroupId}, " +
        $"accessGroup={options.AccessGroupId}, nameLength={name?.Length.ToString() ?? "unchanged"}, " +
        $"emailLength={email?.Length.ToString() ?? "unchanged"}";

    private sealed record BioStarUserState(
        bool Disabled,
        string? UserGroupId,
        IReadOnlyList<string> AccessGroupIds,
        DateTimeOffset? ExpiryDateTime,
        BioStarResponseShape Shape);

    private enum BioStarResponseShape
    {
        Direct,
        UppercaseUserWrapper,
        LowercaseUserWrapper
    }
}
