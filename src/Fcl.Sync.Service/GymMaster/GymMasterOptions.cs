using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.GymMaster;

public sealed class GymMasterOptions
{
    public const string SectionName = "GymMaster";

    public string SiteName { get; init; } = "";

    public string ApiKey { get; init; } = "";

    public string PortalMembersUrl { get; init; } = "";
}
