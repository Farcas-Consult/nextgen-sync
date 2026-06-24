using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.GymMaster;

public sealed class GymMasterClient : IGymMasterClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly GymMasterOptions options;
    private readonly ILogger<GymMasterClient> logger;
    private readonly AuthenticationHeaderValue gatekeeperAuthorization;
    private readonly string? portalMembersUrl;

    public GymMasterClient(HttpClient httpClient, IOptions<GymMasterOptions> options, ILogger<GymMasterClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;

        this.httpClient.BaseAddress = new Uri(this.options.GatekeeperBaseUrl.TrimEnd('/') + "/");
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{this.options.SiteName}:{this.options.ApiKey}"));
        gatekeeperAuthorization = new AuthenticationHeaderValue("Basic", credentials);
        this.httpClient.DefaultRequestHeaders.Authorization = gatekeeperAuthorization;
        this.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        portalMembersUrl = GetPortalMembersUrl(this.options);
    }

    public async Task<GymMasterMember?> GetMemberAsync(long memberId, long? companyId, CancellationToken cancellationToken)
    {
        if (portalMembersUrl is not null)
        {
            var members = await GetPortalMembersAsync(companyId, cancellationToken);
            return members.FirstOrDefault(member => member.MemberId == memberId);
        }

        var uri = BuildMembersUri(memberId, timestamp: null, lastId: null, companyId);
        var response = await GetMembersPageAsync(uri, cancellationToken);
        var member = response.Members.FirstOrDefault(m => m.MemberId == memberId || m.Id == memberId);

        return member is null ? null : await EnrichAsync(Map(member), cancellationToken);
    }

    public async Task<IReadOnlyList<GymMasterMember>> GetCurrentMembersAsync(CancellationToken cancellationToken)
    {
        if (portalMembersUrl is not null)
        {
            return await GetPortalMembersAsync(companyId: null, cancellationToken);
        }

        var members = new List<GymMasterMember>();
        long? lastId = null;

        for (var page = 1; page <= options.MaxSyncPages; page++)
        {
            var uri = BuildMembersUri(memberId: null, timestamp: null, lastId, companyId: null);
            var response = await GetMembersPageAsync(uri, cancellationToken);

            if (response.Members.Count == 0)
            {
                break;
            }

            foreach (var member in response.Members)
            {
                members.Add(await EnrichAsync(Map(member), cancellationToken));
            }

            var nextLastId = response.Members.Max(m => m.MemberId ?? m.Id);

            if (nextLastId is null || nextLastId == lastId)
            {
                break;
            }

            lastId = nextLastId;
        }

        return members;
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
        if (portalMembersUrl is null)
        {
            throw new InvalidOperationException("Portal members URL is not configured.");
        }

        if (companyId is null)
        {
            return portalMembersUrl;
        }

        var separator = portalMembersUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{portalMembersUrl}{separator}companyid={Uri.EscapeDataString(companyId.Value.ToString())}";
    }

    private async Task<GymMasterMember> EnrichAsync(GymMasterMember member, CancellationToken cancellationToken)
    {
        if (!options.StaffApi.Enabled)
        {
            return member;
        }

        if (string.IsNullOrWhiteSpace(options.StaffApi.BaseUrl))
        {
            logger.LogWarning("GymMaster staff API enrichment is enabled but no StaffApi:BaseUrl is configured.");
            return member;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildStaffMemberUri(member.MemberId));
            request.Headers.Authorization = BuildStaffAuthorization();

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("GymMaster staff API did not find member {MemberId}.", member.MemberId);
                return member;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "GymMaster staff API returned {StatusCode} for member {MemberId}: {Body}",
                    (int)response.StatusCode,
                    member.MemberId,
                    body);
                return member;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var profile = await JsonSerializer.DeserializeAsync<StaffMemberProfile>(stream, JsonOptions, cancellationToken);

            return profile is null ? member : Merge(member, profile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "GymMaster staff API enrichment failed for member {MemberId}.", member.MemberId);
            return member;
        }
    }

    private Uri BuildStaffMemberUri(long memberId)
    {
        var baseUrl = options.StaffApi.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl), $"member/{memberId}");
    }

    private AuthenticationHeaderValue BuildStaffAuthorization()
    {
        if (!string.IsNullOrWhiteSpace(options.StaffApi.AuthorizationValue))
        {
            return new AuthenticationHeaderValue(options.StaffApi.AuthorizationScheme, options.StaffApi.AuthorizationValue);
        }

        return gatekeeperAuthorization;
    }

    private static GymMasterMember Merge(GymMasterMember member, StaffMemberProfile profile)
    {
        return member with
        {
            FirstName = Coalesce(profile.FirstName, member.FirstName),
            Surname = Coalesce(profile.Surname, member.Surname),
            Gender = Coalesce(profile.Gender, member.Gender),
            Email = Coalesce(profile.Email, member.Email),
            MobilePhone = Coalesce(profile.PhoneCell, member.MobilePhone),
            JoinDate = profile.JoinDate ?? member.JoinDate,
            CompanyId = profile.CompanyId ?? member.CompanyId
        };
    }

    private static string? Coalesce(string? preferred, string? fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }

    private async Task<GatekeeperMembersResponse> GetMembersPageAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"GymMaster returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<GatekeeperMembersResponse>(stream, JsonOptions, cancellationToken);

        return payload ?? new GatekeeperMembersResponse();
    }

    private static string BuildMembersUri(long? memberId, double? timestamp, long? lastId, long? companyId)
    {
        var query = new List<string>();

        if (memberId is not null)
        {
            query.Add($"memberid={Uri.EscapeDataString(memberId.Value.ToString())}");
        }

        if (timestamp is not null)
        {
            query.Add($"timestamp={Uri.EscapeDataString(timestamp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }

        if (lastId is not null)
        {
            query.Add($"last_id={Uri.EscapeDataString(lastId.Value.ToString())}");
        }

        if (companyId is not null)
        {
            query.Add($"companyid={Uri.EscapeDataString(companyId.Value.ToString())}");
        }

        return query.Count == 0 ? "members" : "members?" + string.Join("&", query);
    }

    private GymMasterMember Map(GatekeeperMember source)
    {
        var (firstName, surname) = SplitName(source.Name);

        return new GymMasterMember
        {
            MemberId = source.MemberId ?? source.Id ?? throw new InvalidOperationException("GymMaster member payload did not include memberid or id."),
            CompanyId = source.CompanyId,
            FirstName = firstName,
            Surname = surname,
            Gender = source.Gender,
            Status = source.Code,
            Owing = source.Owe?.Value ?? 0,
            Email = null,
            MobilePhone = null,
            JoinDate = GetEarliestStartDate(source.Membership)
        };
    }

    private static GymMasterMember Map(PortalMember source)
    {
        return new GymMasterMember
        {
            MemberId = source.Id ?? throw new InvalidOperationException("GymMaster portal member payload did not include id."),
            CompanyId = source.CompanyId,
            FirstName = source.FirstName,
            Surname = source.Surname,
            Gender = source.Gender,
            Status = source.Status,
            Owing = source.OwingValue,
            Email = source.Email,
            MobilePhone = source.PhoneCell,
            JoinDate = source.JoinDate
        };
    }

    private static string? GetPortalMembersUrl(GymMasterOptions options)
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
            return null;
        }

        return $"https://{options.SiteName}.gymmasteronline.com/portal/api/v1/members?api_key={Uri.EscapeDataString(options.ApiKey)}";
    }

    private static (string? FirstName, string? Surname) SplitName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, null);
        }

        var parts = name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? (parts[0], null) : (parts[0], parts[1]);
    }

    private static DateOnly? GetEarliestStartDate(IReadOnlyList<GatekeeperMembership> memberships)
    {
        return memberships
            .Select(m => m.StartDate?.ToDateOnly())
            .Where(d => d is not null)
            .Order()
            .FirstOrDefault();
    }
}

internal sealed record GatekeeperMembersResponse
{
    [JsonPropertyName("lastsync")]
    public JsonElement? LastSync { get; init; }

    [JsonPropertyName("members")]
    public IReadOnlyList<GatekeeperMember> Members { get; init; } = [];
}

internal sealed record GatekeeperMember
{
    [JsonPropertyName("memberid")]
    public long? MemberId { get; init; }

    [JsonPropertyName("companyid")]
    public long? CompanyId { get; init; }

    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("gender")]
    public string? Gender { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("owe")]
    public GymMasterDecimal? Owe { get; init; }

    [JsonPropertyName("membership")]
    public IReadOnlyList<GatekeeperMembership> Membership { get; init; } = [];
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

    public decimal OwingValue
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Owing))
            {
                return 0;
            }

            var cleaned = new string(Owing.Where(c => char.IsDigit(c) || c is '.' or '-' or '+').ToArray());

            return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }
    }
}

internal sealed record GymMasterDecimal
{
    [JsonPropertyName("__decimal__")]
    public string? Raw { get; init; }

    public decimal Value => decimal.TryParse(Raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
        ? value
        : 0;
}

internal sealed record GatekeeperMembership
{
    [JsonPropertyName("startdate")]
    public GymMasterDate? StartDate { get; init; }
}

internal sealed record GymMasterDate
{
    [JsonPropertyName("__date__")]
    public string? Raw { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("month")]
    public int? Month { get; init; }

    [JsonPropertyName("day")]
    public int? Day { get; init; }

    public DateOnly? ToDateOnly()
    {
        if (!string.IsNullOrWhiteSpace(Raw) && DateOnly.TryParse(Raw, out var parsed))
        {
            return parsed;
        }

        if (Year is null || Month is null || Day is null)
        {
            return null;
        }

        return new DateOnly(Year.Value, Month.Value, Day.Value);
    }
}

internal sealed record StaffMemberProfile
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("firstname")]
    public string? FirstName { get; init; }

    [JsonPropertyName("surname")]
    public string? Surname { get; init; }

    [JsonPropertyName("gender")]
    public string? Gender { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("phonecell")]
    public string? PhoneCell { get; init; }

    [JsonPropertyName("joindate")]
    public DateOnly? JoinDate { get; init; }

    [JsonPropertyName("companyid")]
    public long? CompanyId { get; init; }
}
