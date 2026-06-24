using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class AccessProviderOptions
{
    public const string SectionName = "AccessProvider";

    [Required]
    public string Type { get; init; } = "ZKBio";
}
