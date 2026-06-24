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
}
