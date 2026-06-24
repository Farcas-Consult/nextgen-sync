namespace Fcl.Sync.Service.AccessProviders;

public sealed class NoopAccessProvider(ILogger<NoopAccessProvider> logger) : IAccessProvider
{
    public string Name => "Noop";

    public Task<AccessApplyResult> ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        logger.LogDebug("Noop access provider accepted command for PIN {Pin}.", command.Pin);
        return Task.FromResult(AccessApplyResult.Applied("Noop accepted command."));
    }
}
