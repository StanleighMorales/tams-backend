using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Push;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-97: stored notifications are pushed to the recipient and stamped delivered_at.</summary>
[Collection("api")]
public class NoticeDeliveryTests(ApiFixture api)
{
    sealed class Recorder(bool needsTokens) : IApprovalPushSender
    {
        public readonly List<Guid> Sent = [];
        public bool NeedsTokens => needsTokens;
        public Task<IReadOnlyList<string>> SendAsync(ApprovalPush push, IReadOnlyList<string> tokens, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SendNoticeAsync(NoticePush push, IReadOnlyList<string> tokens, CancellationToken ct)
        {
            Sent.Add(push.NotificationId);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    async Task<Guid> NewNotice(TamsDbContext db)
    {
        var admin = await api.As("ADMIN");
        var me = (await admin.GetAsync($"/api/users?q={ApiFixture.AdminEmail}").Ok()).GetProperty("items")[0].Id();
        var n = new Notification { Id = Guid.NewGuid(), RecipientUserId = me, Type = "LOAN_OVERDUE", Title = "t", Body = "b", Payload = """{"x":1}""", CreatedAt = DateTimeOffset.UtcNow };
        db.Notifications.Add(n);
        await db.SaveChangesAsync();
        return n.Id;
    }

    [Fact]
    public async Task A_notice_is_pushed_once_and_stamped_delivered()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var id = await NewNotice(db);
        var sender = new Recorder(needsTokens: false);

        await NoticeDelivery.DeliverAsync(db, sender, DateTimeOffset.UtcNow, default);
        await NoticeDelivery.DeliverAsync(db, sender, DateTimeOffset.UtcNow, default);

        Assert.Single(sender.Sent, id);
        Assert.NotNull((await db.Notifications.FindAsync(id))!.DeliveredAt);
    }

    [Fact]
    public async Task A_recipient_without_a_device_token_is_left_undelivered_when_the_sender_needs_tokens()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var id = await NewNotice(db);
        var sender = new Recorder(needsTokens: true);

        await NoticeDelivery.DeliverAsync(db, sender, DateTimeOffset.UtcNow, default);

        Assert.DoesNotContain(id, sender.Sent);
        Assert.Null((await db.Notifications.FindAsync(id))!.DeliveredAt);
    }
}
