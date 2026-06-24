using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Persistence;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Reconciliation;

public sealed class HourlyReconciliationWorker(
    IOptions<ReconciliationOptions> options,
    IGymMasterClient gymMasterClient,
    IAccessPolicy accessPolicy,
    ILocalSyncStore store,
    IAccessProvider accessProvider,
    ILogger<HourlyReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.IntervalHours));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var members = await gymMasterClient.GetCurrentMembersAsync(cancellationToken);

            foreach (var member in members)
            {
                var decision = accessPolicy.Decide(member);
                await store.UpsertMemberAsync(member, decision, cancellationToken);
                var command = AccessPersonCommand.From(member, decision);
                var commandId = await store.RecordAccessCommandAsync(accessProvider.Name, command.Pin, command, cancellationToken);

                try
                {
                    await accessProvider.ApplyAsync(command, cancellationToken);
                    await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Applied, null, cancellationToken);
                }
                catch (Exception ex)
                {
                    await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, ex.Message, cancellationToken);
                    await store.RecordIntegrationErrorAsync(accessProvider.Name, ex.Message, ex.ToString(), cancellationToken);
                    logger.LogError(ex, "Access provider {AccessProvider} failed for member {MemberId}.", accessProvider.Name, member.MemberId);
                }
            }

            await store.RecordSyncRunAsync(startedAt, DateTimeOffset.UtcNow, members.Count, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hourly reconciliation failed.");
        }
    }
}
