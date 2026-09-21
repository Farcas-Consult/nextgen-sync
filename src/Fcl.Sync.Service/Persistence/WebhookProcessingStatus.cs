namespace Fcl.Sync.Service.Persistence;

public enum WebhookProcessingStatus
{
    Received,
    Processing,
    Completed,
    Failed
}

public enum WebhookBeginResult
{
    Started,
    AlreadyProcessing,
    AlreadyCompleted,
    Stale
}
