using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.GymMaster;

public sealed class GymMasterOptions
{
    public const string SectionName = "GymMaster";

    [Required]
    public string SiteName { get; init; } = "";

    [Required]
    public string ApiKey { get; init; } = "";

    [Required]
    public string GatekeeperBaseUrl { get; init; } = "";

    public string PortalMembersUrl { get; init; } = "";

    public int MaxSyncPages { get; init; } = 100;

    public StaffApiOptions StaffApi { get; init; } = new();
}

public sealed class StaffApiOptions
{
    public bool Enabled { get; init; }

    public string BaseUrl { get; init; } = "";

    public string AuthorizationScheme { get; init; } = "Basic";

    public string? AuthorizationValue { get; init; }
}
