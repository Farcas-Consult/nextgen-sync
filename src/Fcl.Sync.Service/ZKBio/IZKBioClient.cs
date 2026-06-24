using Fcl.Sync.Service.AccessProviders;

namespace Fcl.Sync.Service.ZKBio;

public interface IZKBioClient
{
    Task<AccessApplyResult> ApplyPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyPeopleAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken);
}
