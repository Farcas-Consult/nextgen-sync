using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class ConfiguredAccessProvider(
    IOptions<AccessProviderOptions> options,
    IServiceProvider serviceProvider) : IBulkAccessProvider
{
    public string Name => Current.Name;

    private IAccessProvider Current => options.Value.Type.Trim().ToLowerInvariant() switch
    {
        "noop" or "none" => serviceProvider.GetRequiredService<NoopAccessProvider>(),
        "zkbio" => serviceProvider.GetRequiredService<ZKBioAccessProvider>(),
        var unknown => throw new InvalidOperationException($"Unknown access provider '{unknown}'. Supported values: ZKBio, Noop.")
    };

    public Task<AccessApplyResult> ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return Current.ApplyAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyBatchAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        if (Current is IBulkAccessProvider bulkProvider)
        {
            return await bulkProvider.ApplyBatchAsync(commands, cancellationToken);
        }

        var results = new Dictionary<string, AccessApplyResult>(StringComparer.Ordinal);

        foreach (var command in commands)
        {
            results[command.Pin] = await Current.ApplyAsync(command, cancellationToken);
        }

        return results;
    }
}
