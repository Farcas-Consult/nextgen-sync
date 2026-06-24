using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class ConfiguredAccessProvider(
    IOptions<AccessProviderOptions> options,
    IServiceProvider serviceProvider) : IAccessProvider
{
    public string Name => Current.Name;

    private IAccessProvider Current => options.Value.Type.Trim().ToLowerInvariant() switch
    {
        "noop" or "none" => serviceProvider.GetRequiredService<NoopAccessProvider>(),
        "zkbio" => serviceProvider.GetRequiredService<ZKBioAccessProvider>(),
        var unknown => throw new InvalidOperationException($"Unknown access provider '{unknown}'. Supported values: ZKBio, Noop.")
    };

    public Task ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return Current.ApplyAsync(command, cancellationToken);
    }
}
