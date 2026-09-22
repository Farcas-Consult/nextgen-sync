using Fcl.Sync.Service.ZKBio;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class ZKBioAccessProvider(
    IZKBioClient zkbioClient,
    IOptions<ZKBioOptions> options) : IBulkAccessProvider, IAccessProviderStateHasher, IAccessProviderMemberFilter
{
    public string Name => "ZKBio";

    public Task<AccessApplyResult> ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return zkbioClient.ApplyPersonAsync(Map(command), cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyBatchAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        return zkbioClient.ApplyPeopleAsync(commands.Select(Map).ToList(), cancellationToken);
    }

    public string GetDesiredStateHash(AccessPersonCommand command) => Map(command).SyncHash;

    public bool HandlesCompany(long? companyId) =>
        options.Value.CompanyIds is null or { Count: 0 } ||
        companyId is not null && options.Value.CompanyIds.Contains(companyId.Value);

    private AccessPersonCommand Map(AccessPersonCommand command)
    {
        var mapping = command.Entitlement switch
        {
            "LadiesAndStudio" => (options.Value.LadiesAndStudioAccessLevelIds, options.Value.LadiesDepartmentCode),
            "Mens" => (options.Value.MensAccessLevelIds, options.Value.MensDepartmentCode),
            _ => ("null", options.Value.MensDepartmentCode)
        };

        return command.WithProviderAccess(mapping.Item1, mapping.Item2);
    }
}
