using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class ConfiguredAccessProvider(
    IOptions<AccessProviderOptions> options,
    IServiceProvider serviceProvider,
    ILogger<ConfiguredAccessProvider> logger) : IBulkAccessProvider, IAccessProviderStateHasher, IAccessProviderMemberFilter
{
    public string Name => Current.Name;

    private IAccessProvider Current => options.Value.Type.Trim().ToLowerInvariant() switch
    {
        "noop" or "none" => serviceProvider.GetRequiredService<NoopAccessProvider>(),
        "zkbio" => serviceProvider.GetRequiredService<ZKBioAccessProvider>(),
        "biostar" => serviceProvider.GetRequiredService<BioStarAccessProvider>(),
        var unknown => throw new InvalidOperationException(
            $"Unknown access provider '{unknown}'. Supported values: ZKBio, BioStar, Noop.")
    };

    public async Task<AccessApplyResult> ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return await MemberSyncLock.ExecuteIfCurrentAsync(
            command.Pin,
            command.GeneratedAt,
            () => Current.ApplyAsync(command, cancellationToken),
            cancellationToken)
            ?? AccessApplyResult.Superseded("Superseded by a newer member update.");
    }

    public async Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyBatchAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        if (Current is IBulkAccessProvider bulkProvider && Current is not BioStarAccessProvider)
        {
            return await bulkProvider.ApplyBatchAsync(commands, cancellationToken);
        }

        var results = new Dictionary<string, AccessApplyResult>(StringComparer.Ordinal);
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            try
            {
                results[command.Pin] = await ApplyAsync(command, cancellationToken);
                if ((index + 1) % 100 == 0)
                {
                    logger.LogInformation(
                        "{ProviderName} reconciliation progress: {CompletedCount}/{TotalCount} members processed.",
                        Name,
                        index + 1,
                        commands.Count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                for (var remaining = index; remaining < commands.Count; remaining++)
                {
                    results[commands[remaining].Pin] = AccessApplyResult.Failed(
                        "Provider batch was cancelled before this member was processed.");
                }
                break;
            }
        }
        return results;
    }

    public string GetDesiredStateHash(AccessPersonCommand command) =>
        Current is IAccessProviderStateHasher hasher ? hasher.GetDesiredStateHash(command) : command.SyncHash;

    public bool HandlesCompany(long? companyId) =>
        Current is not IAccessProviderMemberFilter filter || filter.HandlesCompany(companyId);
}
