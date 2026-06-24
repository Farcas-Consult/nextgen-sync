namespace Fcl.Sync.Service.AccessProviders;

public interface IBulkAccessProvider : IAccessProvider
{
    Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyBatchAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken);
}
