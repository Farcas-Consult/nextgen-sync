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
        var syncRunId = await store.StartSyncRunAsync(startedAt, cancellationToken);
        var membersChecked = 0;
        var commandIdsByPin = new Dictionary<string, long>(StringComparer.Ordinal);

        logger.LogInformation("Started hourly reconciliation run {SyncRunId}.", syncRunId);

        try
        {
            await store.MarkStalePendingAccessCommandsAsync(
                $"Marked stale because sync run {syncRunId} started before the prior pending command completed.",
                cancellationToken);

            logger.LogInformation("Fetching current members from GymMaster for sync run {SyncRunId}.", syncRunId);
            var members = await gymMasterClient.GetCurrentMembersAsync(cancellationToken);
            membersChecked = members.Count;
            logger.LogInformation("Fetched {MemberCount} GymMaster members for sync run {SyncRunId}.", members.Count, syncRunId);

            var commands = new List<AccessPersonCommand>(members.Count);

            foreach (var member in members)
            {
                var decision = accessPolicy.Decide(member);
                await store.UpsertMemberAsync(member, decision, cancellationToken);
                var command = AccessPersonCommand.From(member, decision);
                var commandId = await store.RecordAccessCommandAsync(accessProvider.Name, command.Pin, command, cancellationToken);
                commands.Add(command);
                commandIdsByPin[command.Pin] = commandId;
            }

            logger.LogInformation(
                "Stored {MemberCount} members locally and queued {CommandCount} access commands for sync run {SyncRunId}.",
                members.Count,
                commands.Count,
                syncRunId);

            try
            {
                using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerTimeout.CancelAfter(TimeSpan.FromMinutes(options.Value.AccessProviderTimeoutMinutes));

                logger.LogInformation(
                    "Applying {CommandCount} access commands through {AccessProvider} for sync run {SyncRunId}. Timeout is {TimeoutMinutes} minutes.",
                    commands.Count,
                    accessProvider.Name,
                    syncRunId,
                    options.Value.AccessProviderTimeoutMinutes);

                var results = accessProvider is IBulkAccessProvider bulkProvider
                    ? await bulkProvider.ApplyBatchAsync(commands, providerTimeout.Token)
                    : await ApplyOneByOneAsync(commands, providerTimeout.Token);

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

                var appliedCount = results.Values.Count(result => result.Outcome == AccessApplyOutcome.Applied);
                var skippedCount = results.Values.Count(result => result.Outcome == AccessApplyOutcome.Skipped);
                logger.LogInformation(
                    "Access provider {AccessProvider} finished sync run {SyncRunId}. Applied {AppliedCount}, skipped {SkippedCount}.",
                    accessProvider.Name,
                    syncRunId,
                    appliedCount,
                    skippedCount);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var message = $"Access provider {accessProvider.Name} timed out after {options.Value.AccessProviderTimeoutMinutes} minutes.";
                await store.RecordIntegrationErrorAsync(accessProvider.Name, message, ex.ToString(), CancellationToken.None);
                logger.LogError(ex, "{Message}", message);

                foreach (var commandId in commandIdsByPin.Values)
                {
                    await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, message, CancellationToken.None);
                }

                await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked, SyncRunStatus.Failed, message, CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                await store.RecordIntegrationErrorAsync(accessProvider.Name, ex.Message, ex.ToString(), cancellationToken);
                logger.LogError(ex, "Access provider {AccessProvider} failed during hourly reconciliation.", accessProvider.Name);

                foreach (var commandId in commandIdsByPin.Values)
                {
                    await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, ex.Message, cancellationToken);
                }

                await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked, SyncRunStatus.Failed, ex.Message, cancellationToken);
                return;
            }

            await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked, SyncRunStatus.Completed, null, cancellationToken);
            logger.LogInformation("Completed hourly reconciliation run {SyncRunId} for {MembersChecked} members.", syncRunId, membersChecked);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked, SyncRunStatus.Failed, "Service stopped during reconciliation.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hourly reconciliation failed.");
            await store.RecordIntegrationErrorAsync("Reconciliation", ex.Message, ex.ToString(), CancellationToken.None);
            await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked, SyncRunStatus.Failed, ex.Message, CancellationToken.None);

            foreach (var commandId in commandIdsByPin.Values)
            {
                await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, ex.Message, CancellationToken.None);
            }
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
