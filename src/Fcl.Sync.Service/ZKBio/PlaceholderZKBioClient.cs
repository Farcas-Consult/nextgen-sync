using Fcl.Sync.Service.AccessProviders;

namespace Fcl.Sync.Service.ZKBio;

public sealed class PlaceholderZKBioClient(ILogger<PlaceholderZKBioClient> logger) : IZKBioClient
{
    public Task UpsertPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        logger.LogWarning("ZKBio client is not configured yet. Would upsert PIN {Pin}.", command.Pin);
        return Task.CompletedTask;
    }
}
