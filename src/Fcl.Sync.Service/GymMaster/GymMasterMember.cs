namespace Fcl.Sync.Service.GymMaster;

public sealed record GymMasterMember
{
    public required long MemberId { get; init; }
    public long? CompanyId { get; init; }
    public string? FirstName { get; init; }
    public string? Surname { get; init; }
    public string? Gender { get; init; }
    public string? Status { get; init; }
    public decimal Owing { get; init; }
    public string? Email { get; init; }
    public string? MobilePhone { get; init; }
    public DateOnly? JoinDate { get; init; }
}
