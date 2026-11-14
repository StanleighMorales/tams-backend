using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Tams.Api.Live;

namespace Tams.Api.Push;

/// <summary>Development stand-in: logs the payload and emits it as "approval.push" to the instructor's SignalR group.</summary>
public sealed class FakeApprovalPushSender(IHubContext<LiveHub> hub, ILogger<FakeApprovalPushSender> log) : IApprovalPushSender
{
    public const string EventName = "approval.push";
    public const string NoticeEventName = "notice.push";

    public bool NeedsTokens => false;

    public async Task<IReadOnlyList<string>> SendNoticeAsync(NoticePush push, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var message = push.ToMessage();
        log.LogInformation("Fake notice push to user {UserId} ({Tokens} token(s)): {Payload}",
            push.UserId, tokens.Count, JsonSerializer.Serialize(message, JsonSerializerOptions.Web));
        await hub.Clients.Group(LiveHub.UserGroup(push.UserId)).SendAsync(NoticeEventName, message, ct);
        return [];
    }

    public async Task<IReadOnlyList<string>> SendAsync(ApprovalPush push, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        var message = push.ToMessage();
        log.LogInformation("Fake approval push to user {UserId} ({Tokens} token(s)): {Payload}",
            push.UserId, tokens.Count, JsonSerializer.Serialize(message, JsonSerializerOptions.Web));
        await hub.Clients.Group(LiveHub.UserGroup(push.UserId)).SendAsync(EventName, message, ct);
        return [];
    }
}

/// <summary>Used when push is switched off or FCM has no credentials.</summary>
public sealed class DisabledApprovalPushSender(ILogger<DisabledApprovalPushSender> log) : IApprovalPushSender
{
    public bool NeedsTokens => true;

    public Task<IReadOnlyList<string>> SendNoticeAsync(NoticePush push, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        log.LogDebug("Push disabled; skipped notice {NotificationId}.", push.NotificationId);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<IReadOnlyList<string>> SendAsync(ApprovalPush push, IReadOnlyList<string> tokens, CancellationToken ct)
    {
        log.LogDebug("Approval push disabled; skipped session {SessionId}.", push.SessionId);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
