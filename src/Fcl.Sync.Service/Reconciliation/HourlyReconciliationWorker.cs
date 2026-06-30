using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.AccessProviders;
using Fcl.Sync.Service.GymMaster;
using Fcl.Sync.Service.Persistence;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Reconciliation;

public sealed class HourlyReconciliationWorker(
    IOptions<ReconciliationOptions> options,
    IOptions<HistoryRetentionOptions> historyRetentionOptions,
    IGymMasterClient gymMasterClient,
    IAccessPolicy accessPolicy,
    ILocalSyncStore store,
    IAccessProvider accessProvider,
    ILogger<HourlyReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastFullAuditAt = null;
        DateTimeOffset? lastHistoryCleanupAt = null;

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.IntervalHours));

        if (options.Value.FullAuditOnStartup)
        {
            await RunOnceAsync(SyncRunMode.FullAudit, stoppingToken);
            lastFullAuditAt = DateTimeOffset.UtcNow;
        }
        else
        {
            await RunOnceAsync(SyncRunMode.Fast, stoppingToken);
        }

        await CleanupHistoryIfDueAsync(lastHistoryCleanupAt, stoppingToken);
        lastHistoryCleanupAt = DateTimeOffset.UtcNow;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var shouldRunFullAudit = lastFullAuditAt is null ||
                                     DateTimeOffset.UtcNow - lastFullAuditAt.Value >= TimeSpan.FromHours(options.Value.FullAuditIntervalHours);

            if (shouldRunFullAudit)
            {
                await RunOnceAsync(SyncRunMode.FullAudit, stoppingToken);
                lastFullAuditAt = DateTimeOffset.UtcNow;
            }
            else
            {
                await RunOnceAsync(SyncRunMode.Fast, stoppingToken);
            }

            if (await CleanupHistoryIfDueAsync(lastHistoryCleanupAt, stoppingToken))
            {
                lastHistoryCleanupAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task<bool> CleanupHistoryIfDueAsync(DateTimeOffset? lastCleanupAt, CancellationToken cancellationToken)
    {
        var retention = historyRetentionOptions.Value;

        if (!retention.Enabled)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (lastCleanupAt is not null &&
            now - lastCleanupAt.Value < TimeSpan.FromHours(retention.CleanupIntervalHours))
        {
            return false;
        }

        try
        {
            await store.CleanupHistoryAsync(retention, now, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SQLite history cleanup failed; sync will continue and cleanup will retry later.");
            return false;
        }
    }

    private async Task RunOnceAsync(SyncRunMode mode, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var syncRunId = await store.StartSyncRunAsync(mode, startedAt, cancellationToken);
        var membersChecked = 0;
        var commandIdsByPin = new Dictionary<string, long>(StringComparer.Ordinal);

        logger.LogInformation("Started {SyncRunMode} reconciliation run {SyncRunId}.", mode, syncRunId);

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

            var commandsForProvider = await SelectCommandsForProviderAsync(mode, commands, cancellationToken);
            var providerPins = commandsForProvider.Select(command => command.Pin).ToHashSet(StringComparer.Ordinal);
            var locallySkippedCommands = commands
                .Where(command => !providerPins.Contains(command.Pin))
                .ToList();

            foreach (var command in locallySkippedCommands)
            {
                await store.MarkAccessCommandAsync(
                    commandIdsByPin[command.Pin],
                    AccessCommandStatus.Skipped,
                    "Fresh local ZKBio confirmation matched desired state.",
                    cancellationToken);
            }

            logger.LogInformation(
                "Stored {MemberCount} members locally and queued {CommandCount} access commands for sync run {SyncRunId}. Provider will process {ProviderCommandCount}; local cache skipped {LocalSkipCount}.",
                members.Count,
                commands.Count,
                syncRunId,
                commandsForProvider.Count,
                locallySkippedCommands.Count);

            try
            {
                using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerTimeout.CancelAfter(TimeSpan.FromMinutes(options.Value.AccessProviderTimeoutMinutes));

                logger.LogInformation(
                    "Applying {CommandCount} access commands through {AccessProvider} for sync run {SyncRunId}. Timeout is {TimeoutMinutes} minutes.",
                    commandsForProvider.Count,
                    accessProvider.Name,
                    syncRunId,
                    options.Value.AccessProviderTimeoutMinutes);

                var results = commandsForProvider.Count == 0
                    ? new Dictionary<string, AccessApplyResult>(StringComparer.Ordinal)
                    : accessProvider is IBulkAccessProvider bulkProvider
                        ? await bulkProvider.ApplyBatchAsync(commandsForProvider, providerTimeout.Token)
                        : await ApplyOneByOneAsync(commandsForProvider, providerTimeout.Token);

                foreach (var command in commandsForProvider)
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

                    if (result.Outcome is AccessApplyOutcome.Applied or AccessApplyOutcome.Skipped)
                    {
                        await store.UpsertZKBioCacheAsync(command, DateTimeOffset.UtcNow, cancellationToken);
                    }
                }

                var appliedCount = results.Values.Count(result => result.Outcome == AccessApplyOutcome.Applied);
                var skippedCount = results.Values.Count(result => result.Outcome == AccessApplyOutcome.Skipped);
                var failedCount = results.Values.Count(result => result.Outcome == AccessApplyOutcome.Failed);
                logger.LogInformation(
                    "Access provider {AccessProvider} finished sync run {SyncRunId}. Applied {AppliedCount}, provider skipped {ProviderSkippedCount}, failed {FailedCount}, local cache skipped {LocalSkipCount}.",
                    accessProvider.Name,
                    syncRunId,
                    appliedCount,
                    skippedCount,
                    failedCount,
                    locallySkippedCommands.Count);
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
            logger.LogInformation("Completed {SyncRunMode} reconciliation run {SyncRunId} for {MembersChecked} members.", mode, syncRunId, membersChecked);
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

    private async Task<IReadOnlyList<AccessPersonCommand>> SelectCommandsForProviderAsync(
        SyncRunMode mode,
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        if (mode == SyncRunMode.FullAudit || accessProvider.Name != "ZKBio")
        {
            return commands;
        }

        var freshAfter = DateTimeOffset.UtcNow.AddHours(-options.Value.ZKBioCacheMaxAgeHours);
        var desiredHashesByPin = commands.ToDictionary(command => command.Pin, command => command.SyncHash, StringComparer.Ordinal);
        var freshConfirmedPins = await store.GetFreshConfirmedPinsAsync(desiredHashesByPin, freshAfter, cancellationToken);

        logger.LogInformation(
            "Fast sync found {FreshConfirmedCount}/{CommandCount} commands already confirmed in local ZKBio cache since {FreshAfter}.",
            freshConfirmedPins.Count,
            commands.Count,
            freshAfter);

        return commands
            .Where(command => !freshConfirmedPins.Contains(command.Pin))
            .ToList();
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
