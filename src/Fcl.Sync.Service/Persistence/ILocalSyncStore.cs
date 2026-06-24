using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Webhooks;

namespace Fcl.Sync.Service.Persistence;

public interface ILocalSyncStore
{
    Task<bool> TryRecordWebhookAsync(GymMasterWebhookEvent webhookEvent, CancellationToken cancellationToken);

    Task UpsertMemberAsync(GymMasterMember member, AccessDecision accessDecision, CancellationToken cancellationToken);

    Task<long> RecordAccessCommandAsync(
        string providerName,
        string pin,
        object command,
        CancellationToken cancellationToken);

    Task MarkAccessCommandAsync(
        long commandId,
        AccessCommandStatus status,
        string? errorMessage,
        CancellationToken cancellationToken);

    Task MarkStalePendingAccessCommandsAsync(string reason, CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetFreshConfirmedPinsAsync(
        IReadOnlyDictionary<string, string> desiredHashesByPin,
        DateTimeOffset freshAfter,
        CancellationToken cancellationToken);

    Task UpsertZKBioCacheAsync(
        AccessPersonCommand command,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    Task<long> StartSyncRunAsync(SyncRunMode mode, DateTimeOffset startedAt, CancellationToken cancellationToken);

    Task CompleteSyncRunAsync(
        long syncRunId,
        DateTimeOffset completedAt,
        int membersChecked,
        SyncRunStatus status,
        string? errorMessage,
        CancellationToken cancellationToken);

    Task RecordIntegrationErrorAsync(string source, string message, string? details, CancellationToken cancellationToken);
}
