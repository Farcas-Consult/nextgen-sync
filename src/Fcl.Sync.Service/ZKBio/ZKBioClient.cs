using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Fcl.Sync.Service.AccessProviders;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.ZKBio;

public sealed class ZKBioClient : IZKBioClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;
    private readonly ZKBioOptions options;
    private readonly ILogger<ZKBioClient> logger;

    public ZKBioClient(HttpClient httpClient, IOptions<ZKBioOptions> options, ILogger<ZKBioClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;

        if (string.IsNullOrWhiteSpace(this.options.BaseUrl) ||
            string.IsNullOrWhiteSpace(this.options.AccessToken))
        {
            throw new InvalidOperationException("ZKBio provider requires ZKBio:BaseUrl and ZKBio:AccessToken.");
        }

        this.httpClient.BaseAddress = new Uri(this.options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<AccessApplyResult> ApplyPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var current = await GetPersonAsync(command.Pin, cancellationToken);

            if (current is not null && Matches(command, current))
            {
                return AccessApplyResult.Skipped("ZKBio person already matches desired state.");
            }

            var message = await UpsertPersonAsync(command, cancellationToken);
            return AccessApplyResult.Applied(message ?? (current is null ? "Created ZKBio person." : "Updated ZKBio person."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ZKBio failed to apply PIN {Pin}.", command.Pin);
            return AccessApplyResult.Failed(ex.Message);
        }
    }

    public async Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyPeopleAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting ZKBio batch apply for {CommandCount} people.", commands.Count);

        var results = new Dictionary<string, AccessApplyResult>(StringComparer.Ordinal);
        var currentPeople = await GetBatchPersonsAsync(commands.Select(command => command.Pin).ToList(), cancellationToken);
        var processedCount = 0;

        foreach (var command in commands)
        {
            try
            {
                currentPeople.TryGetValue(command.Pin, out var current);

                if (current is not null && Matches(command, current))
                {
                    results[command.Pin] = AccessApplyResult.Skipped("ZKBio person already matches desired state.");
                    processedCount++;
                    continue;
                }

                var message = await UpsertPersonAsync(command, cancellationToken);
                results[command.Pin] = AccessApplyResult.Applied(message ?? (current is null ? "Created ZKBio person." : "Updated ZKBio person."));
                processedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[command.Pin] = AccessApplyResult.Failed(ex.Message);
                processedCount++;
                logger.LogWarning(ex, "ZKBio failed to apply PIN {Pin}; continuing with the next person.", command.Pin);
            }

            if (processedCount % 100 == 0)
            {
                logger.LogInformation("ZKBio apply progress: {ProcessedCount}/{CommandCount} people processed.", processedCount, commands.Count);
            }
        }

        logger.LogInformation("Finished ZKBio batch apply for {CommandCount} people.", commands.Count);
        return results;
    }

    private async Task<ZKBioPerson?> GetPersonAsync(string pin, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(BuildUrl($"api/person/get/{Uri.EscapeDataString(pin)}", new Dictionary<string, string>
        {
            ["_t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        }), cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ZKBio get person {pin} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var result = JsonSerializer.Deserialize<ZKBioApiResponse<ZKBioPerson>>(body, JsonOptions);

        if (result is not null && result.Code < 0)
        {
            logger.LogWarning("ZKBio get person {Pin} returned API code {Code}: {Message}", pin, result.Code, result.Message);
            return null;
        }

        return result?.Data;
    }

    private async Task<IReadOnlyDictionary<string, ZKBioPerson>> GetBatchPersonsAsync(
        IReadOnlyList<string> pins,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, ZKBioPerson>(StringComparer.Ordinal);

        foreach (var chunk in pins.Chunk(options.BatchSize))
        {
            var pinList = string.Join(',', chunk);
            var url = BuildUrl("api/person/getPersonList", new Dictionary<string, string>
            {
                ["pins"] = pinList,
                ["pageNo"] = "1",
                ["pageSize"] = chunk.Length.ToString()
            });

            logger.LogInformation("Fetching {ChunkSize} existing ZKBio people for comparison.", chunk.Length);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ZKBio getPersonList returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            var apiResult = JsonSerializer.Deserialize<ZKBioApiResponse<JsonElement>>(body, JsonOptions);

            if (apiResult is not null && apiResult.Code < 0)
            {
                throw new InvalidOperationException($"ZKBio getPersonList API error {apiResult.Code}: {apiResult.Message}");
            }

            foreach (var person in ExtractPersons(apiResult?.Data))
            {
                if (!string.IsNullOrWhiteSpace(person.Pin))
                {
                    results[person.Pin] = person;
                }
            }
        }

        logger.LogInformation("Fetched {Count} existing ZKBio people for comparison.", results.Count);
        return results;
    }

    private async Task<string?> UpsertPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        var payload = ZKBioPersonPayload.From(command, includeEmail: true);
        var result = await SendUpsertAsync(command.Pin, payload, cancellationToken);

        if (result is not null && result.Code < 0 && IsDuplicateEmailError(result))
        {
            logger.LogWarning(
                "ZKBio rejected PIN {Pin} because the mailbox already exists. Retrying without email so access can still sync.",
                command.Pin);

            var retryPayload = ZKBioPersonPayload.From(command, includeEmail: false);
            var retryResult = await SendUpsertAsync(command.Pin, retryPayload, cancellationToken);

            if (retryResult is not null && retryResult.Code < 0)
            {
                throw new InvalidOperationException($"ZKBio upsert person {command.Pin} API error {retryResult.Code}: {retryResult.Message}");
            }

            logger.LogInformation("ZKBio accepted upsert for PIN {Pin} without email.", command.Pin);
            return "Updated ZKBio person without email because the mailbox already exists.";
        }

        if (result is not null && result.Code < 0)
        {
            throw new InvalidOperationException($"ZKBio upsert person {command.Pin} API error {result.Code}: {result.Message}");
        }

        logger.LogInformation("ZKBio accepted upsert for PIN {Pin}.", command.Pin);
        return null;
    }

    private async Task<ZKBioApiResponse<object>?> SendUpsertAsync(
        string pin,
        ZKBioPersonPayload payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(BuildUrl("api/person/add"), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ZKBio upsert person {pin} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return JsonSerializer.Deserialize<ZKBioApiResponse<object>>(body, JsonOptions);
    }

    private static bool IsDuplicateEmailError(ZKBioApiResponse<object> result)
    {
        return result.Code == -71 ||
               result.Message?.Contains("mailbox already exists", StringComparison.OrdinalIgnoreCase) == true ||
               result.Message?.Contains("email already exists", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool Matches(AccessPersonCommand desired, ZKBioPerson current)
    {
        return Same(ZKBioPersonPayload.SanitizeName(desired.Name, desired.Pin), current.Name) &&
               Same(ZKBioPersonPayload.SanitizeOptionalName(desired.LastName), current.LastName) &&
               Same(desired.DepartmentCode, current.DepartmentCode) &&
               desired.IsDisabled == current.IsDisabled &&
               SameAccessLevels(desired.AccessLevelIds, current.AccessLevelIds) &&
               Same(desired.Email, current.Email) &&
               Same(desired.MobilePhone, current.MobilePhone);
    }

    private static bool Same(string? desired, string? current)
    {
        return string.Equals(Normalize(desired), Normalize(current), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value is "null" ? "" : value.Trim();
    }

    private static bool SameAccessLevels(string? desired, string? current)
    {
        var desiredSet = NormalizeAccessLevels(desired);
        var currentSet = NormalizeAccessLevels(current);
        return desiredSet.SetEquals(currentSet);
    }

    private static HashSet<string> NormalizeAccessLevels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "null")
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.Equals(item, "null", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ZKBioPerson> ExtractPersons(JsonElement? data)
    {
        if (data is null)
        {
            return [];
        }

        var element = data.Value;

        if (element.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<IReadOnlyList<ZKBioPerson>>(element.GetRawText(), JsonOptions) ?? [];
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var propertyName in new[] { "list", "data", "persons", "result" })
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<IReadOnlyList<ZKBioPerson>>(property.GetRawText(), JsonOptions) ?? [];
            }
        }

        return [];
    }

    private string BuildUrl(string endpoint, IReadOnlyDictionary<string, string>? parameters = null)
    {
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var builder = new StringBuilder(endpoint)
            .Append(separator)
            .Append("access_token=")
            .Append(Uri.EscapeDataString(options.AccessToken));

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                builder.Append('&')
                    .Append(Uri.EscapeDataString(key))
                    .Append('=')
                    .Append(Uri.EscapeDataString(value));
            }
        }

        return builder.ToString();
    }
}

internal sealed record ZKBioApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

internal sealed record ZKBioPerson
{
    [JsonPropertyName("pin")]
    public string Pin { get; init; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; init; }

    [JsonPropertyName("accLevelIds")]
    public string? AccessLevelIds { get; init; }

    [JsonPropertyName("deptCode")]
    public string? DepartmentCode { get; init; }

    [JsonPropertyName("isDisabled")]
    public bool IsDisabled { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("mobilePhone")]
    public string? MobilePhone { get; init; }
}

internal sealed record ZKBioPersonPayload
{
    [JsonPropertyName("pin")]
    public required string Pin { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; init; }

    [JsonPropertyName("accLevelIds")]
    public required string AccessLevelIds { get; init; }

    [JsonPropertyName("deptCode")]
    public required string DepartmentCode { get; init; }

    [JsonPropertyName("isDisabled")]
    public required bool IsDisabled { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("mobilePhone")]
    public string? MobilePhone { get; init; }

    [JsonPropertyName("joindate")]
    public string? JoinDate { get; init; }

    public static ZKBioPersonPayload From(AccessPersonCommand command, bool includeEmail)
    {
        return new ZKBioPersonPayload
        {
            Pin = command.Pin,
            Name = SanitizeName(command.Name, command.Pin),
            LastName = SanitizeOptionalName(command.LastName),
            AccessLevelIds = command.AccessLevelIds,
            DepartmentCode = command.DepartmentCode,
            IsDisabled = command.IsDisabled,
            Email = includeEmail ? command.Email : null,
            MobilePhone = command.MobilePhone,
            JoinDate = command.JoinDate?.ToString("O")
        };
    }

    internal static string SanitizeName(string? value, string fallback)
    {
        var sanitized = SanitizeOptionalName(value);
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    internal static string? SanitizeOptionalName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(character);
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character))
            {
                pendingSpace = true;
            }
        }

        var result = builder.ToString().Normalize(NormalizationForm.FormC);
        return result.Length == 0 ? null : result;
    }
}
