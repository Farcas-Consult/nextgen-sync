using Fcl.Sync.Service.AccessProviders;

namespace Fcl.Sync.Service.ZKBio;

public sealed class PlaceholderZKBioClient(ILogger<PlaceholderZKBioClient> logger) : IZKBioClient
{
    public Task<AccessApplyResult> ApplyPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        logger.LogWarning("ZKBio client is not configured yet. Would upsert PIN {Pin}.", command.Pin);
        return Task.FromResult(AccessApplyResult.Applied("Placeholder accepted command."));
    }

    public Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyPeopleAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        var results = commands.ToDictionary(
            command => command.Pin,
            _ => AccessApplyResult.Applied("Placeholder accepted command."),
            StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyDictionary<string, AccessApplyResult>>(results);
    }
}
