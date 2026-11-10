using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;

namespace Tams.Api.Push;

/// <summary>
/// Sends an approval push in the background, after the kiosk request has committed. Failures are logged,
/// never thrown, so the kiosk call is not affected. Tokens the provider reports as invalid are deleted.
/// </summary>
public sealed class ApprovalPushDispatcher(IServiceScopeFactory scopes, ILogger<ApprovalPushDispatcher> log)
{
    public void Enqueue(ApprovalPush push) => _ = Task.Run(() => DeliverAsync(push));

    public async Task DeliverAsync(ApprovalPush push, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
            var sender = scope.ServiceProvider.GetRequiredService<IApprovalPushSender>();
            var tokens = await db.DeviceTokens.AsNoTracking().Where(t => t.UserId == push.UserId).Select(t => t.Token).ToListAsync(ct);
            var invalid = await sender.SendAsync(push, tokens, ct);
            if (invalid.Count > 0)
            {
                var removed = await db.DeviceTokens.Where(t => invalid.Contains(t.Token)).ExecuteDeleteAsync(ct);
                log.LogInformation("Removed {Count} invalid FCM token(s) for user {UserId}.", removed, push.UserId);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Approval push for session {SessionId} failed.", push.SessionId);
        }
    }
}

public static class PushSetup
{
    /// <summary>Push:Provider = Fcm | Fake | None. Unset: Fake in Development, otherwise Fcm (which disables itself without credentials).</summary>
    public static IServiceCollection AddTamsPush(this IServiceCollection services, IConfiguration cfg, IHostEnvironment env)
    {
        services.AddSingleton<ApprovalPushDispatcher>();
        var provider = cfg["Push:Provider"] ?? (env.IsDevelopment() ? "Fake" : "Fcm");
        switch (provider.ToUpperInvariant())
        {
            case "FAKE":
                services.AddSingleton<IApprovalPushSender, FakeApprovalPushSender>();
                break;
            case "FCM":
                services.AddHttpClient();
                services.AddSingleton<IApprovalPushSender>(sp =>
                    FcmCredentials.Load(cfg, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Tams.Push")) is { } c
                        ? new FcmApprovalPushSender(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), c, sp.GetRequiredService<ILogger<FcmApprovalPushSender>>())
                        : new DisabledApprovalPushSender(sp.GetRequiredService<ILogger<DisabledApprovalPushSender>>()));
                break;
            case "NONE":
                services.AddSingleton<IApprovalPushSender, DisabledApprovalPushSender>();
                break;
            default:
                throw new InvalidOperationException($"Push:Provider '{provider}' is not one of Fcm, Fake, None.");
        }
        return services;
    }
}
