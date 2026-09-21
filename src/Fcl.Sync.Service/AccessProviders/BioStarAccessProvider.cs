using Fcl.Sync.Service.BioStar;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Fcl.Sync.Service.AccessProviders;

public sealed class BioStarAccessProvider(
    IBioStarClient bioStarClient,
    IOptions<BioStarOptions> options) : IBulkAccessProvider, IAccessProviderStateHasher, IAccessProviderMemberFilter
{
    public string Name => "BioStar";

    public Task<AccessApplyResult> ApplyAsync(AccessPersonCommand command, CancellationToken cancellationToken)
    {
        return bioStarClient.ApplyPersonAsync(command, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, AccessApplyResult>> ApplyBatchAsync(
        IReadOnlyList<AccessPersonCommand> commands,
        CancellationToken cancellationToken)
    {
        return bioStarClient.ApplyPeopleAsync(commands, cancellationToken);
    }

    public string GetDesiredStateHash(AccessPersonCommand command)
    {
        var canonical = string.Join('\u001f',
            command.Pin,
            command.IsDisabled ? "1" : "0",
            options.Value.UserGroupId.Trim(),
            options.Value.AccessGroupId.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public bool HandlesCompany(long? companyId) =>
        options.Value.CompanyIds.Count == 0 ||
        companyId is not null && options.Value.CompanyIds.Contains(companyId.Value);
}
