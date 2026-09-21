using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.ZKBio;

public sealed class ZKBioOptions
{
    public const string SectionName = "ZKBio";

    [Required]
    public string BaseUrl { get; init; } = "";

    [Required]
    public string AccessToken { get; init; } = "";

    public bool AllowInvalidServerCertificate { get; init; }

    public int BatchSize { get; init; } = 100;

    public string MensAccessLevelIds { get; init; } = "2c9e83828d5efdf0018d5efede2f0442";

    public string LadiesAndStudioAccessLevelIds { get; init; } = "2c9e838298504c94019855f6048f6337,2c9e8382985f525a019863ffe0e44e26";

    public string MensDepartmentCode { get; init; } = "1";

    public string LadiesDepartmentCode { get; init; } = "7";
}
