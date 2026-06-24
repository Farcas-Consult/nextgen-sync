using Fcl.Sync.Service.AccessProviders;

namespace Fcl.Sync.Service.ZKBio;

public interface IZKBioClient
{
    Task UpsertPersonAsync(AccessPersonCommand command, CancellationToken cancellationToken);
}
