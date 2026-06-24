using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class ConfiguredAccessProvider(
    IOptions<AccessProviderOptions> options,
    NoopAccessProvider noopAccessProvider,
    ZKBioAccessProvider zkbioAccessProvider) : IAccessProvider
{
    public string Name => Current.Name;

    private IAccessProvider Current => options.Value.Type.Trim().ToLowerInvariant() switch
    {
        "noop" or "none" => noopAccessProvider,
        "zkbio" => zkbioAccessProvider,
        var unknown => throw new InvalidOperationException($"Unknown access provider '{unknown}'. Supported values: ZKBio, Noop.")
    };

    public Task ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return Current.ApplyAsync(command, cancellationToken);
    }
}
