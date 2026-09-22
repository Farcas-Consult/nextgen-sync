namespace Fcl.Sync.Service.BioStar;

public sealed class BioStarOptions
{
    public const string SectionName = "BioStar";

    public string BaseUrl { get; init; } = "";

    public string LoginId { get; init; } = "";

    public string Password { get; init; } = "";

    public string UserGroupId { get; init; } = "1052";

    public string AccessGroupId { get; init; } = "3";

    public DateTimeOffset StartDateTime { get; init; } = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset ExpiryDateTime { get; init; } = new(2030, 12, 31, 23, 59, 0, TimeSpan.Zero);

    public bool AllowInvalidServerCertificate { get; init; }

    /// <summary>
    /// Sends numeric member IDs as JSON numbers when possible. Set to false for BioStar
    /// installations that require user_id to always be a JSON string.
    /// </summary>
    public bool UseNumericUserIdWhenPossible { get; init; }

    public int MaxNameLength { get; init; } = 48;

    public int MaxEmailLength { get; init; } = 128;

    public IReadOnlyList<long> CompanyIds { get; init; } = [3];
}
