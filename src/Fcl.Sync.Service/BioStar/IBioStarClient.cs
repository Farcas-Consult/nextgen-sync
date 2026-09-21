using Fcl.Sync.Service.AccessProviders;

namespace Fcl.Sync.Service.BioStar;

public interface IBioStarClient
{
    Task<AccessApplyResult> ApplyPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyPeopleAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken);
}
