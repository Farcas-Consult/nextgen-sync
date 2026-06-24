using Fcl.Sync.Service.GymMaster;

namespace Fcl.Sync.Service.AccessControl;

public interface IAccessPolicy
{
    AccessDecision Decide(GymMasterMember member);
}
