using System.Text.Json.Serialization;

namespace Fcl.Sync.Service.Webhooks;

public sealed record GymMasterWebhookEvent
{
    [JsonPropertyName("event_id")]
    public long EventId { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = "";

    [JsonPropertyName("event_timestamp")]
    public DateTimeOffset EventTimestamp { get; init; }

    [JsonPropertyName("processed_at")]
    public DateTimeOffset? ProcessedAt { get; init; }

    [JsonPropertyName("payload")]
    public GymMasterWebhookPayload Payload { get; init; } = new();
}

public sealed record GymMasterWebhookPayload
{
    [JsonPropertyName("memberid")]
    public long? MemberId { get; init; }

    [JsonPropertyName("companyid")]
    public long? CompanyId { get; init; }

    [JsonExtensionData]
    public IDictionary<string, object>? Extra { get; init; }
}
