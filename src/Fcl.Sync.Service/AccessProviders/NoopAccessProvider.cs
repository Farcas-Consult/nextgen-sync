namespace Fcl.Sync.Service.AccessProviders;

public sealed class NoopAccessProvider(ILogger<NoopAccessProvider> logger) : IAccessProvider
{
    public string Name => "Noop";

    public Task ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Noop access provider accepted command for PIN {Pin}.", command.Pin);
        return Task.CompletedTask;
    }
}
