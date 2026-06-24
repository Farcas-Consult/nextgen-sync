using Fcl.Sync.Service.AccessControl;
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

    Task RecordSyncRunAsync(DateTimeOffset startedAt, DateTimeOffset completedAt, int membersChecked, CancellationToken cancellationToken);

    Task RecordIntegrationErrorAsync(string source, string message, string? details, CancellationToken cancellationToken);
}
