namespace Fcl.Sync.Service.GymMaster;

public interface IGymMasterClient
{
    Task<GymMasterMember?> GetMemberAsync(long memberId, long? companyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GymMasterMember>> GetCurrentMembersAsync(CancellationToken cancellationToken);
}
