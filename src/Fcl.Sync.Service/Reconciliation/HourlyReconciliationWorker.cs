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
            var commands = new List<AccessPersonCommand>(members.Count);
            var commandIdsByPin = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var member in members)
            {
                var decision = accessPolicy.Decide(member);
                await store.UpsertMemberAsync(member, decision, cancellationToken);
                var command = AccessPersonCommand.From(member, decision);
                var commandId = await store.RecordAccessCommandAsync(accessProvider.Name, command.Pin, command, cancellationToken);
                commands.Add(command);
                commandIdsByPin[command.Pin] = commandId;
            }

            try
            {
                var results = accessProvider is IBulkAccessProvider bulkProvider
                    ? await bulkProvider.ApplyBatchAsync(commands, cancellationToken)
                    : await ApplyOneByOneAsync(commands, cancellationToken);

                foreach (var command in commands)
                {
                    var commandId = commandIdsByPin[command.Pin];
                    var result = results.TryGetValue(command.Pin, out var foundResult)
                        ? foundResult
                        : AccessApplyResult.Applied("Provider did not return a result.");

                    await store.MarkAccessCommandAsync(
                        commandId,
                        ToCommandStatus(result),
                        result.Message,
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await store.RecordIntegrationErrorAsync(accessProvider.Name, ex.Message, ex.ToString(), cancellationToken);
                logger.LogError(ex, "Access provider {AccessProvider} failed during hourly reconciliation.", accessProvider.Name);

                foreach (var commandId in commandIdsByPin.Values)
                {
                    await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, ex.Message, cancellationToken);
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

    private async Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyOneByOneAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, AccessApplyResult>(StringComparer.Ordinal);

        foreach (var command in commands)
        {
            results[command.Pin] = await accessProvider.ApplyAsync(command, cancellationToken);
        }

        return results;
    }

    private static AccessCommandStatus ToCommandStatus(AccessApplyResult result)
    {
        return result.Outcome == AccessApplyOutcome.Skipped
            ? AccessCommandStatus.Skipped
            : AccessCommandStatus.Applied;
    }
}
