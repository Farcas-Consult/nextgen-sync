using Fcl.Sync.Service.AccessControl;
using Fcl.Sync.Service.GymMaster;
using System.Security.Cryptography;
using System.Text;

namespace Fcl.Sync.Service.AccessProviders;

public sealed record AccessPersonCommand
{
    public required string Pin { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public long? CompanyId { get; init; }
    public required string Name { get; init; }
    public string? LastName { get; init; }
    public required string Entitlement { get; init; }
    public string AccessLevelIds { get; init; } = "";
    public string DepartmentCode { get; init; } = "";
    public required bool IsDisabled { get; init; }
    public string? Email { get; init; }
    public string? MobilePhone { get; init; }
    public DateOnly? JoinDate { get; init; }
    public required string AccessHash { get; init; }
    public required string ProfileHash { get; init; }
    public required string SyncHash { get; init; }

    public static AccessPersonCommand From(GymMasterMember member, AccessDecision decision)
    {
        var fullName = $"{member.FirstName} {member.Surname}".Trim();
        var command = new AccessPersonCommand
        {
            Pin = member.MemberId.ToString(),
            GeneratedAt = DateTimeOffset.UtcNow,
            CompanyId = member.CompanyId,
            Name = string.IsNullOrWhiteSpace(fullName) ? member.MemberId.ToString() : fullName,
            LastName = member.Surname,
            Entitlement = decision.Entitlement,
            IsDisabled = decision.IsDisabled,
            Email = member.Email,
            MobilePhone = member.MobilePhone,
            JoinDate = member.JoinDate,
            AccessHash = "",
            ProfileHash = "",
            SyncHash = ""
        };

        var accessHash = HashParts(
            command.Pin,
            Normalize(command.Entitlement),
            command.IsDisabled ? "1" : "0");

        var profileHash = HashParts(
            Normalize(command.Name),
            Normalize(command.LastName),
            NormalizeEmail(command.Email),
            NormalizePhone(command.MobilePhone),
            command.JoinDate?.ToString("yyyy-MM-dd") ?? "");

        return command with
        {
            AccessHash = accessHash,
            ProfileHash = profileHash,
            SyncHash = HashParts(accessHash, profileHash)
        };
    }

    public AccessPersonCommand WithProviderAccess(string accessLevelIds, string departmentCode)
    {
        var accessHash = HashParts(
            Pin,
            Normalize(Entitlement),
            NormalizeAccessLevels(accessLevelIds),
            Normalize(departmentCode),
            IsDisabled ? "1" : "0");

        return this with
        {
            AccessLevelIds = accessLevelIds,
            DepartmentCode = departmentCode,
            AccessHash = accessHash,
            SyncHash = HashParts(accessHash, ProfileHash)
        };
    }

    private static string HashParts(params string[] parts)
    {
        var canonical = string.Join('\u001f', parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
            ? ""
            : value.Trim();
    }

    private static string NormalizeEmail(string? value)
    {
        return Normalize(value).ToLowerInvariant();
    }

    private static string NormalizePhone(string? value)
    {
        var normalized = Normalize(value);
        return new string(normalized.Where(c => char.IsDigit(c) || c is '+').ToArray());
    }

    private static string NormalizeAccessLevels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return string.Join(
            ',',
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.Equals(item, "null", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase));
    }
}
