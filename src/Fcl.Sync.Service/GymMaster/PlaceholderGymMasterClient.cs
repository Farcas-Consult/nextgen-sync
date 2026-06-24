namespace Fcl.Sync.Service.GymMaster;

public sealed class PlaceholderGymMasterClient(ILogger<PlaceholderGymMasterClient> logger) : IGymMasterClient
{
    public Task<GymMasterMember?> GetMemberAsync(long memberId, long? companyId, CancellationToken cancellationToken)
    {
        logger.LogWarning("GymMaster API client is not configured yet. Member {MemberId} was not fetched.", memberId);
        return Task.FromResult<GymMasterMember?>(null);
    }

    public Task<IReadOnlyList<GymMasterMember>> GetCurrentMembersAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("GymMaster full sync API is not configured yet.");
        return Task.FromResult<IReadOnlyList<GymMasterMember>>([]);
    }
}
