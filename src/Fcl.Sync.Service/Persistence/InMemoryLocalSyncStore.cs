using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Webhooks;

namespace Fcl.Sync.Service.Persistence;

public sealed class InMemoryLocalSyncStore(ILogger<InMemoryLocalSyncStore> logger) : ILocalSyncStore
{
    public Task<bool> TryRecordWebhookAsync(GymMasterWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        logger.LogInformation("Recorded webhook {EventId} of type {EventType}.", webhookEvent.EventId, webhookEvent.EventType);
        return Task.FromResult(true);
    }

    public Task UpsertMemberAsync(GymMasterMember member, AccessDecision accessDecision, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Stored member {MemberId} with entitlement {Entitlement}, disabled {IsDisabled}.",
            member.MemberId,
            accessDecision.Entitlement,
            accessDecision.IsDisabled);

        return Task.CompletedTask;
    }

    public Task<long> RecordAccessCommandAsync(string providerName, string pin, object command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Recorded access command for provider {ProviderName}, PIN {Pin}.", providerName, pin);
        return Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public Task MarkAccessCommandAsync(long commandId, AccessCommandStatus status, string? errorMessage, CancellationToken cancellationToken)
    {
        logger.LogInformation("Marked access command {CommandId} as {Status}.", commandId, status);
        return Task.CompletedTask;
    }

    public Task MarkStalePendingAccessCommandsAsync(string reason, CancellationToken cancellationToken)
    {
        logger.LogInformation("Marked stale pending access commands as failed. {Reason}", reason);
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetFreshConfirmedPinsAsync(
        string providerName,
        IReadOnlyDictionary<string, string> desiredHashesByPin,
        DateTimeOffset freshAfter,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    public Task UpsertProviderCacheAsync(
        string providerName,
        string providerType,
        string pin,
        string desiredHash,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Updated {ProviderName} cache for PIN {Pin}.", providerName, pin);
        return Task.CompletedTask;
    }

    public Task<long> StartSyncRunAsync(SyncRunMode mode, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        logger.LogInformation("Started {Mode} sync run {SyncRunId}.", mode, id);
        return Task.FromResult(id);
    }

    public Task CompleteSyncRunAsync(
        long syncRunId,
        DateTimeOffset completedAt,
        int membersChecked,
        SyncRunStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Completed sync run {SyncRunId} with status {Status} for {MembersChecked} members. {ErrorMessage}",
            syncRunId,
            status,
            membersChecked,
            errorMessage);

        return Task.CompletedTask;
    }

    public Task RecordIntegrationErrorAsync(string source, string message, string? details, CancellationToken cancellationToken)
    {
        logger.LogError("Integration error from {Source}: {Message}. {Details}", source, message, details);
        return Task.CompletedTask;
    }

    public Task CleanupHistoryAsync(HistoryRetentionOptions options, DateTimeOffset now, CancellationToken cancellationToken)
    {
        logger.LogInformation("Skipped history cleanup for in-memory store.");
        return Task.CompletedTask;
    }
}
