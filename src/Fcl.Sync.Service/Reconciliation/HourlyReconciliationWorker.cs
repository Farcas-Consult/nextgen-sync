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
            lastFullAuditAt = DateTimeOffset.UtcNow;
        }

        await CleanupHistoryIfDueAsync(lastHistoryCleanupAt, stoppingToken);
        lastHistoryCleanupAt = DateTimeOffset.UtcNow;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var fullAudit = lastFullAuditAt is null ||
                DateTimeOffset.UtcNow - lastFullAuditAt.Value >= TimeSpan.FromHours(options.Value.FullAuditIntervalHours);
            await RunOnceAsync(fullAudit ? SyncRunMode.FullAudit : SyncRunMode.Fast, stoppingToken);
            if (fullAudit)
            {
                lastFullAuditAt = DateTimeOffset.UtcNow;
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
        if (!retention.Enabled || lastCleanupAt is not null &&
            DateTimeOffset.UtcNow - lastCleanupAt.Value < TimeSpan.FromHours(retention.CleanupIntervalHours))
        {
            return false;
        }

        try
        {
            await store.CleanupHistoryAsync(retention, DateTimeOffset.UtcNow, cancellationToken);
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
        var snapshotVersion = DateTimeOffset.UtcNow;
        var syncRunId = await store.StartSyncRunAsync(mode, snapshotVersion, cancellationToken);
        var membersChecked = 0;
        var commandIdsByPin = new Dictionary<string, long>(StringComparer.Ordinal);

        try
        {
            await store.MarkStalePendingAccessCommandsAsync(
                $"Marked stale because sync run {syncRunId} started before the prior pending command completed.",
                cancellationToken);

            var fetchedMembers = await gymMasterClient.GetCurrentMembersAsync(cancellationToken);
            var members = fetchedMembers.Where(member => HandlesCompany(member.CompanyId)).ToList();
            var duplicateMemberId = members.GroupBy(member => member.MemberId).FirstOrDefault(group => group.Count() > 1);
            if (duplicateMemberId is not null)
            {
                throw new InvalidOperationException(
                    $"GymMaster returned duplicate member ID {duplicateMemberId.Key} within the configured company scope.");
            }
            membersChecked = members.Count;
            var commands = new List<AccessPersonCommand>(members.Count);

            foreach (var member in members)
            {
                var decision = accessPolicy.Decide(member);
                await store.UpsertMemberAsync(member, decision, cancellationToken);
                var command = AccessPersonCommand.From(member, decision) with { GeneratedAt = snapshotVersion };
                commandIdsByPin[command.Pin] = await store.RecordAccessCommandAsync(
                    accessProvider.Name, command.Pin, command, cancellationToken);
                commands.Add(command);
            }

            var selected = await SelectCommandsForProviderAsync(mode, commands, cancellationToken);
            var selectedPins = selected.Select(command => command.Pin).ToHashSet(StringComparer.Ordinal);
            foreach (var command in commands.Where(command => !selectedPins.Contains(command.Pin)))
            {
                await store.MarkAccessCommandAsync(commandIdsByPin[command.Pin], AccessCommandStatus.Skipped,
                    "Fresh local provider confirmation matched desired state.", cancellationToken);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(options.Value.AccessProviderTimeoutMinutes));
            var results = selected.Count == 0
                ? new Dictionary<string, AccessApplyResult>(StringComparer.Ordinal)
                : accessProvider is IBulkAccessProvider bulk
                    ? await bulk.ApplyBatchAsync(selected, timeout.Token)
                    : await ApplyOneByOneAsync(selected, timeout.Token);

            var failedCount = 0;
            foreach (var command in selected)
            {
                var result = results.TryGetValue(command.Pin, out var found)
                    ? found
                    : AccessApplyResult.Failed("Provider did not return a result.");
                await store.MarkAccessCommandAsync(commandIdsByPin[command.Pin], ToCommandStatus(result), result.Message, cancellationToken);
                if (result.Outcome is AccessApplyOutcome.Applied or AccessApplyOutcome.Skipped)
                {
                    await store.UpsertProviderCacheAsync(accessProvider.Name, accessProvider.Name, command.Pin,
                        GetDesiredHash(command), DateTimeOffset.UtcNow, cancellationToken);
                }
                else if (result.Outcome == AccessApplyOutcome.Failed)
                {
                    failedCount++;
                }
            }

            var status = failedCount == 0 ? SyncRunStatus.Completed : SyncRunStatus.Failed;
            var error = failedCount == 0 ? null : $"{failedCount} {accessProvider.Name} command(s) failed.";
            await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked, status, error, cancellationToken);
            logger.LogInformation("Completed {Mode} reconciliation through {Provider} for {Count} members; {Failed} failed.",
                mode, accessProvider.Name, membersChecked, failedCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked,
                SyncRunStatus.Failed, "Service stopped during reconciliation.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException
                ? $"Access provider {accessProvider.Name} timed out after {options.Value.AccessProviderTimeoutMinutes} minutes."
                : ex.Message;
            logger.LogError(ex, "Reconciliation through {Provider} failed.", accessProvider.Name);
            await store.RecordIntegrationErrorAsync(accessProvider.Name, message, ex.ToString(), CancellationToken.None);
            await store.CompleteSyncRunAsync(syncRunId, DateTimeOffset.UtcNow, membersChecked,
                SyncRunStatus.Failed, message, CancellationToken.None);
            foreach (var commandId in commandIdsByPin.Values)
            {
                await store.MarkAccessCommandAsync(commandId, AccessCommandStatus.Failed, message, CancellationToken.None);
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
        if (mode == SyncRunMode.FullAudit)
        {
            return commands;
        }

        var freshAfter = DateTimeOffset.UtcNow.AddHours(-options.Value.ProviderCacheMaxAgeHours);
        var hashes = commands.ToDictionary(command => command.Pin, GetDesiredHash, StringComparer.Ordinal);
        var confirmed = await store.GetFreshConfirmedPinsAsync(accessProvider.Name, hashes, freshAfter, cancellationToken);
        return commands.Where(command => !confirmed.Contains(command.Pin)).ToList();
    }

    private string GetDesiredHash(AccessPersonCommand command) =>
        accessProvider is IAccessProviderStateHasher hasher ? hasher.GetDesiredStateHash(command) : command.SyncHash;

    private bool HandlesCompany(long? companyId) =>
        accessProvider is not IAccessProviderMemberFilter filter || filter.HandlesCompany(companyId);

    private static AccessCommandStatus ToCommandStatus(AccessApplyResult result) => result.Outcome switch
    {
        AccessApplyOutcome.Skipped => AccessCommandStatus.Skipped,
        AccessApplyOutcome.Superseded => AccessCommandStatus.Skipped,
        AccessApplyOutcome.Failed => AccessCommandStatus.Failed,
        _ => AccessCommandStatus.Applied
    };
}
