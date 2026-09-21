namespace Fcl.Sync.Service.AccessProviders;

public interface IAccessProviderStateHasher
{
    string GetDesiredStateHash(AccessPersonCommand command);
}
