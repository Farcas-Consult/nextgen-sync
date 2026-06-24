namespace Fcl.Sync.Service.AccessProviders;

public interface IAccessProvider
{
    string Name { get; }

    Task ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken);
}
