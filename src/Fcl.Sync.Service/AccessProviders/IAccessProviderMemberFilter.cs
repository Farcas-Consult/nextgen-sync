namespace Fcl.Sync.Service.AccessProviders;

public interface IAccessProviderMemberFilter
{
    bool HandlesCompany(long? companyId);
}
