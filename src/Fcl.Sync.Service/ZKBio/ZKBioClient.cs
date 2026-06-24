using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public async Task UpsertPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        var payload = ZKBioPersonPayload.From(command);
        var url = BuildUrl("api/person/add");
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(url, content, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ZKBio returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var result = JsonSerializer.Deserialize<ZKBioApiResponse<object>>(body, JsonOptions);

        if (result is not null && result.Code < 0)
        {
            throw new InvalidOperationException($"ZKBio API error {result.Code}: {result.Message}");
        }

        logger.LogInformation("ZKBio accepted upsert for PIN {Pin}.", command.Pin);
    }

    private string BuildUrl(string endpoint)
    {
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{endpoint}{separator}access_token={Uri.EscapeDataString(options.AccessToken)}";
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

    public static ZKBioPersonPayload From(AccessPersonCommand command)
    {
        return new ZKBioPersonPayload
        {
            Pin = command.Pin,
            Name = command.Name,
            LastName = command.LastName,
            AccessLevelIds = command.AccessLevelIds,
            DepartmentCode = command.DepartmentCode,
            IsDisabled = command.IsDisabled,
            Email = command.Email,
            MobilePhone = command.MobilePhone,
            JoinDate = command.JoinDate?.ToString("O")
        };
    }
}
