namespace Fcl.Sync.Service.AccessProviders;

public interface IAccessProvider
{
    string Name { get; }

    Task<AccessApplyResult> ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken);
}
