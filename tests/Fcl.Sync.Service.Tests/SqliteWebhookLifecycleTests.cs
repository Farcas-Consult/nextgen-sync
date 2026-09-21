using Fcl.Sync.Service.Persistence;
using Fcl.Sync.Service.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Tests;

public sealed class SqliteWebhookLifecycleTests
{
    [Fact]
    public async Task FailedWebhookCanRetryButCompletedWebhookCannot()
    {
        var (store, path) = CreateStore();
        try
        {
            var webhook = Event(100, 42, DateTimeOffset.Parse("2026-09-21T10:00:00Z"));

            Assert.Equal(WebhookBeginResult.Started, await store.TryBeginWebhookAsync(webhook, default));
            await store.MarkWebhookAsync(webhook.EventId, WebhookProcessingStatus.Failed, "temporary", default);
            Assert.Equal(WebhookBeginResult.Started, await store.TryBeginWebhookAsync(webhook, default));
            await store.MarkWebhookAsync(webhook.EventId, WebhookProcessingStatus.Completed, null, default);
            Assert.Equal(WebhookBeginResult.AlreadyCompleted, await store.TryBeginWebhookAsync(webhook, default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OlderWebhookIsStaleAfterNewerWebhookCompletedForMember()
    {
        var (store, path) = CreateStore();
        try
        {
            var newer = Event(200, 42, DateTimeOffset.Parse("2026-09-21T11:00:00Z"));
            var older = Event(199, 42, DateTimeOffset.Parse("2026-09-21T10:00:00Z"));
            Assert.Equal(WebhookBeginResult.Started, await store.TryBeginWebhookAsync(newer, default));
            await store.MarkWebhookAsync(newer.EventId, WebhookProcessingStatus.Completed, null, default);

            Assert.Equal(WebhookBeginResult.Stale, await store.TryBeginWebhookAsync(older, default));
            Assert.Equal(WebhookBeginResult.AlreadyCompleted, await store.TryBeginWebhookAsync(older, default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (SqliteLocalSyncStore Store, string Path) CreateStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fcl-sync-test-{Guid.NewGuid():N}.db");
        var store = new SqliteLocalSyncStore(
            Options.Create(new SqliteOptions { ConnectionString = $"Data Source={path}" }),
            NullLogger<SqliteLocalSyncStore>.Instance);
        return (store, path);
    }

    private static GymMasterWebhookEvent Event(long eventId, long memberId, DateTimeOffset timestamp) => new()
    {
        EventId = eventId,
        EventType = "member.updated",
        EventTimestamp = timestamp,
        Payload = new GymMasterWebhookPayload { MemberId = memberId, CompanyId = 3 }
    };
}
