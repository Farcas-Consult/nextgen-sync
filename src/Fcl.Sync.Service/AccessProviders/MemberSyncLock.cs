using System.Collections.Concurrent;

namespace Fcl.Sync.Service.AccessProviders;

public static class MemberSyncLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LatestVersions = new(StringComparer.Ordinal);

    public static async ValueTask<IDisposable> AcquireAsync(string pin, CancellationToken cancellationToken)
    {
        var memberLock = Locks.GetOrAdd(pin, static _ => new SemaphoreSlim(1, 1));
        await memberLock.WaitAsync(cancellationToken);
        return new Releaser(memberLock);
    }

    public static async Task<T?> ExecuteIfCurrentAsync<T>(
        string pin,
        DateTimeOffset version,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var memberLock = await AcquireAsync(pin, cancellationToken);
        if (LatestVersions.TryGetValue(pin, out var latest) && version < latest)
        {
            return default;
        }

        LatestVersions.AddOrUpdate(pin, version, (_, existing) => version > existing ? version : existing);
        return await action();
    }

    private sealed class Releaser(SemaphoreSlim memberLock) : IDisposable
    {
        public void Dispose() => memberLock.Release();
    }
}
