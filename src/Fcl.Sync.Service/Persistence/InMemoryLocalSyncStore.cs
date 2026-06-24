using Fcl.Sync.Service.AccessControl;
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
            "Stored member {MemberId} with access {AccessLevelIds}, department {DepartmentCode}, disabled {IsDisabled}.",
            member.MemberId,
            accessDecision.AccessLevelIds,
            accessDecision.DepartmentCode,
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

    public Task RecordSyncRunAsync(DateTimeOffset startedAt, DateTimeOffset completedAt, int membersChecked, CancellationToken cancellationToken)
    {
        logger.LogInformation("Recorded sync run for {MembersChecked} members.", membersChecked);
        return Task.CompletedTask;
    }

    public Task RecordIntegrationErrorAsync(string source, string message, string? details, CancellationToken cancellationToken)
    {
        logger.LogError("Integration error from {Source}: {Message}. {Details}", source, message, details);
        return Task.CompletedTask;
    }
}
