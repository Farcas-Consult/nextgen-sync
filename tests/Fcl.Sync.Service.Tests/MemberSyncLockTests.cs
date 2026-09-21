using Fcl.Sync.Service.AccessProviders;

namespace Fcl.Sync.Service.Tests;

public sealed class MemberSyncLockTests
{
    [Fact]
    public async Task OlderMemberCommandIsSupersededAfterNewerVersionRuns()
    {
        var pin = Guid.NewGuid().ToString("N");
        var newer = DateTimeOffset.UtcNow;
        var calls = 0;

        var newResult = await MemberSyncLock.ExecuteIfCurrentAsync(
            pin, newer, () => Task.FromResult(++calls), default);
        var oldResult = await MemberSyncLock.ExecuteIfCurrentAsync(
            pin, newer.AddMinutes(-1), () => Task.FromResult(++calls), default);

        Assert.Equal(1, newResult);
        Assert.Equal(0, oldResult);
        Assert.Equal(1, calls);
    }
}
