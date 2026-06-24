using System.ComponentModel.DataAnnotations;

namespace Fcl.Sync.Service.Webhooks;

public sealed class GymMasterWebhookOptions
{
    public const string SectionName = "GymMaster:Webhooks";

    [Required]
    public string SecretToken { get; init; } = "";
}
