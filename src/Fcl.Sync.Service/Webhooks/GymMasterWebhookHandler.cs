using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Persistence;

namespace Fcl.Sync.Service.Webhooks;

public sealed class GymMasterWebhookHandler(
    IGymMasterClient gymMasterClient,
    IAccessPolicy accessPolicy,
    ILocalSyncStore store,
    IAccessProvider accessProvider,
    ILogger<GymMasterWebhookHandler> logger)
{
    public async Task HandleAsync(GymMasterWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        var beginResult = await store.TryBeginWebhookAsync(webhookEvent, cancellationToken);

        if (beginResult != WebhookBeginResult.Started)
        {
            logger.LogInformation(
                "Skipping GymMaster webhook {EventId}; persisted disposition is {Disposition}.",
                webhookEvent.EventId,
                beginResult);
            return;
        }

        try
        {
            if (webhookEvent.Payload.MemberId is null)
            {
                logger.LogInformation(
                    "Recorded GymMaster webhook {EventId} of type {EventType}; no member id was supplied.",
                    webhookEvent.EventId,
                    webhookEvent.EventType);
                await store.MarkWebhookAsync(webhookEvent.EventId, WebhookProcessingStatus.Completed, null, cancellationToken);
                return;
            }

            var member = await gymMasterClient.GetMemberAsync(
                webhookEvent.Payload.MemberId.Value,
                webhookEvent.Payload.CompanyId,
                cancellationToken);

            if (member is null)
            {
                throw new InvalidOperationException(
                    $"GymMaster member {webhookEvent.Payload.MemberId} was not found while processing webhook {webhookEvent.EventId}.");
            }

            if (accessProvider is IAccessProviderMemberFilter filter && !filter.HandlesCompany(member.CompanyId))
            {
                logger.LogInformation(
                    "Ignoring GymMaster webhook {EventId} because company {CompanyId} is not handled by {ProviderName}.",
                    webhookEvent.EventId,
                    member.CompanyId,
                    accessProvider.Name);
                await store.MarkWebhookAsync(webhookEvent.EventId, WebhookProcessingStatus.Completed, null, cancellationToken);
                return;
            }

            var decision = accessPolicy.Decide(member);
            await store.UpsertMemberAsync(member, decision, cancellationToken);

            var command = AccessPersonCommand.From(member, decision) with { GeneratedAt = webhookEvent.EventTimestamp };
            var commandId = await store.RecordAccessCommandAsync(accessProvider.Name, command.Pin, command, cancellationToken);
            AccessApplyResult result;
            try
            {
                result = await accessProvider.ApplyAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, ex.Message, CancellationToken.None);
                throw;
            }

            await store.MarkAccessCommandAsync(commandId, ToCommandStatus(result), result.Message, cancellationToken);

            if (result.Outcome is AccessApplyOutcome.Applied or AccessApplyOutcome.Skipped)
            {
                var desiredHash = accessProvider is IAccessProviderStateHasher hasher
                    ? hasher.GetDesiredStateHash(command)
                    : command.SyncHash;
                await store.UpsertProviderCacheAsync(
                    accessProvider.Name, accessProvider.Name, command.Pin, desiredHash, DateTimeOffset.UtcNow, cancellationToken);
            }
            else if (result.Outcome == AccessApplyOutcome.Failed)
            {
                await store.RecordIntegrationErrorAsync(accessProvider.Name, result.Message ?? "Provider command failed.", null, cancellationToken);
                throw new InvalidOperationException(result.Message ?? "Provider command failed.");
            }

            await store.MarkWebhookAsync(webhookEvent.EventId, WebhookProcessingStatus.Completed, null, cancellationToken);

            logger.LogInformation(
                "Processed GymMaster webhook {EventId} for member {MemberId} through {ProviderName}.",
                webhookEvent.EventId,
                webhookEvent.Payload.MemberId,
                accessProvider.Name);
        }
        catch (Exception ex)
        {
            await store.MarkWebhookAsync(webhookEvent.EventId, WebhookProcessingStatus.Failed, ex.Message, CancellationToken.None);
            await store.RecordIntegrationErrorAsync("GymMasterWebhook", ex.Message, ex.ToString(), CancellationToken.None);
            throw;
        }
    }

    private static AccessCommandStatus ToCommandStatus(AccessApplyResult result)
    {
        return result.Outcome switch
        {
            AccessApplyOutcome.Skipped => AccessCommandStatus.Skipped,
            AccessApplyOutcome.Superseded => AccessCommandStatus.Skipped,
            AccessApplyOutcome.Failed => AccessCommandStatus.Failed,
            _ => AccessCommandStatus.Applied
        };
    }
}
