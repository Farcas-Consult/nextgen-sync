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
        var isNewEvent = await store.TryRecordWebhookAsync(webhookEvent, cancellationToken);

        if (!isNewEvent)
        {
            return;
        }

        if (webhookEvent.Payload.MemberId is null)
        {
            logger.LogInformation(
                "Recorded GymMaster webhook {EventId} of type {EventType}; no member id was supplied.",
                webhookEvent.EventId,
                webhookEvent.EventType);
            return;
        }

        var member = await gymMasterClient.GetMemberAsync(
            webhookEvent.Payload.MemberId.Value,
            webhookEvent.Payload.CompanyId,
            cancellationToken);

        if (member is null)
        {
            logger.LogWarning(
                "GymMaster member {MemberId} was not found while processing webhook {EventId}.",
                webhookEvent.Payload.MemberId,
                webhookEvent.EventId);
            return;
        }

        var decision = accessPolicy.Decide(member);
        await store.UpsertMemberAsync(member, decision, cancellationToken);

        var command = AccessPersonCommand.From(member, decision);
        var commandId = await store.RecordAccessCommandAsync(accessProvider.Name, command.Pin, command, cancellationToken);

        try
        {
            logger.LogInformation(
                "Processing GymMaster webhook {EventId} ({EventType}) for member {MemberId}.",
                webhookEvent.EventId,
                webhookEvent.EventType,
                webhookEvent.Payload.MemberId);

            var result = await accessProvider.ApplyAsync(command, cancellationToken);
            await store.MarkAccessCommandAsync(
                commandId,
                ToCommandStatus(result),
                result.Message,
                cancellationToken);

            if (accessProvider.Name == "ZKBio" && result.Outcome is AccessApplyOutcome.Applied or AccessApplyOutcome.Skipped)
            {
                await store.UpsertZKBioCacheAsync(command, DateTimeOffset.UtcNow, cancellationToken);
            }

            logger.LogInformation(
                "Processed GymMaster webhook {EventId} for member {MemberId}; access command {CommandId} is {Status}.",
                webhookEvent.EventId,
                webhookEvent.Payload.MemberId,
                commandId,
                result.Outcome);
        }
        catch (Exception ex)
        {
            await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, ex.Message, cancellationToken);
            await store.RecordIntegrationErrorAsync(accessProvider.Name, ex.Message, ex.ToString(), cancellationToken);
            throw;
        }
    }

    private static AccessCommandStatus ToCommandStatus(AccessApplyResult result)
    {
        return result.Outcome switch
        {
            AccessApplyOutcome.Skipped => AccessCommandStatus.Skipped,
            AccessApplyOutcome.Failed => AccessCommandStatus.Failed,
            _ => AccessCommandStatus.Applied
        };
    }
}
