using Fcl.Sync.Service.ZKBio;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class ZKBioAccessProvider(IZKBioClient zkbioClient) : IBulkAccessProvider
{
    public string Name => "ZKBio";

    public Task<AccessApplyResult> ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return zkbioClient.ApplyPersonAsync(command, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyBatchAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        return zkbioClient.ApplyPeopleAsync(commands, cancellationToken);
    }
}
