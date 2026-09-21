using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.GymMaster;

public sealed class GymMasterClient : IGymMasterClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly string portalMembersUrl;

    public GymMasterClient(HttpClient httpClient, IOptions<GymMasterOptions> options)
    {
        this.httpClient = httpClient;
        portalMembersUrl = BuildPortalMembersUrl(options.Value);
    }

    public async Task<GymMasterMember?> GetMemberAsync(long memberId, long? companyId, CancellationToken cancellationToken)
    {
        if (companyId is not null)
        {
            var companyMembers = await GetPortalMembersAsync(companyId, cancellationToken);
            var companyMember = companyMembers.FirstOrDefault(member => member.MemberId == memberId);

            if (companyMember is not null)
            {
                return companyMember;
            }
        }

        var members = await GetPortalMembersAsync(companyId: null, cancellationToken);
        return members.FirstOrDefault(member => member.MemberId == memberId);
    }

    public async Task<IReadOnlyList<GymMasterMember>> GetCurrentMembersAsync(CancellationToken cancellationToken)
    {
        return await GetPortalMembersAsync(companyId: null, cancellationToken);
    }

    private async Task<IReadOnlyList<GymMasterMember>> GetPortalMembersAsync(long? companyId, CancellationToken cancellationToken)
    {
        var uri = BuildPortalMembersUri(companyId);
        using var response = await httpClient.GetAsync(uri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"GymMaster portal API returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<PortalMembersResponse>(stream, JsonOptions, cancellationToken);

        if (payload is null)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(payload.Error))
        {
            throw new InvalidOperationException($"GymMaster portal API returned error: {payload.Error}");
        }

        return payload.Result
            .Where(member => companyId is null || member.CompanyId == companyId)
            .Select(Map)
            .ToList();
    }

    private string BuildPortalMembersUri(long? companyId)
    {
        if (companyId is null)
        {
            return portalMembersUrl;
        }

        var separator = portalMembersUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{portalMembersUrl}{separator}companyid={Uri.EscapeDataString(companyId.Value.ToString())}";
    }

    private static string BuildPortalMembersUrl(GymMasterOptions options)
    {
        var configuredUrl = string.IsNullOrWhiteSpace(options.PortalMembersUrl)
            ? Environment.GetEnvironmentVariable("GMS_API_URL")
            : options.PortalMembersUrl;

        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl.Trim().Trim('"', '\'');
        }

        if (string.IsNullOrWhiteSpace(options.SiteName) ||
            string.IsNullOrWhiteSpace(options.ApiKey) ||
            options.SiteName is "replace-me" ||
            options.ApiKey is "replace-me")
        {
            throw new InvalidOperationException("GymMaster Portal API requires GymMaster:PortalMembersUrl, or GymMaster:SiteName plus GymMaster:ApiKey.");
        }

        return $"https://{options.SiteName}.gymmasteronline.com/portal/api/v1/members?api_key={Uri.EscapeDataString(options.ApiKey)}";
    }

    private static GymMasterMember Map(PortalMember source)
    {
        var hasValidOwing = source.TryGetOwingValue(out var owing);
        return new GymMasterMember
        {
            MemberId = source.Id ?? throw new InvalidOperationException("GymMaster portal member payload did not include id."),
            CompanyId = source.CompanyId,
            FirstName = source.FirstName,
            Surname = source.Surname,
            Gender = source.Gender,
            Status = source.Status,
            Owing = owing,
            HasValidOwing = hasValidOwing,
            Email = source.Email,
            MobilePhone = source.PhoneCell,
            JoinDate = source.JoinDate
        };
    }
}

internal sealed record PortalMembersResponse
{
    [JsonPropertyName("result")]
    public IReadOnlyList<PortalMember> Result { get; init; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed record PortalMember
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("firstname")]
    public string? FirstName { get; init; }

    [JsonPropertyName("surname")]
    public string? Surname { get; init; }

    [JsonPropertyName("gender")]
    public string? Gender { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("owing")]
    public string? Owing { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("phonecell")]
    public string? PhoneCell { get; init; }

    [JsonPropertyName("joindate")]
    public DateOnly? JoinDate { get; init; }

    [JsonPropertyName("companyid")]
    public long? CompanyId { get; init; }

    public bool TryGetOwingValue(out decimal value)
    {
        if (string.IsNullOrWhiteSpace(Owing))
        {
            value = 0;
            return false;
        }

        var cleaned = new string(Owing.Where(c => char.IsDigit(c) || c is '.' or '-' or '+').ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            value = 0;
            return false;
        }

        return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
