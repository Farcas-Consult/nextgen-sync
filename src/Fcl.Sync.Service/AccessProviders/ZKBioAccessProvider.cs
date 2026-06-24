using Fcl.Sync.Service.ZKBio;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class ZKBioAccessProvider(IZKBioClient zkbioClient) : IAccessProvider
{
    public string Name => "ZKBio";

    public Task ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return zkbioClient.UpsertPersonAsync(command, cancellationToken);
    }
}
