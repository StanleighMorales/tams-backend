using Microsoft.AspNetCore.SignalR;
using Tams.Api.Data;
using Tams.Api.Kiosk;
using Tams.Api.Live;

namespace Tams.Api.Loans;

/// <summary>
/// PRD 6.1 background job. Every 5 s it expires kiosk sessions past their deadline (FR-1.7): the idle
/// timeout at each step, and the approval timeout while waiting for the instructor. Each session is its
/// own unit of work, so one bad row cannot stall the rest.
/// </summary>
public sealed class LoanSweeper(IServiceScopeFactory scopes, IHubContext<LiveHub> hub, ILogger<LoanSweeper> logger) : BackgroundService
{
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await WaitAsync(timer, stoppingToken))
            await RunAsync("kiosk sessions", (db, o, now, ct) => KioskSessions.ExpireStaleAsync(db, o, now, ct), stoppingToken);
    }

    async Task RunAsync(string step, Func<TamsDbContext, Outbox, DateTimeOffset, CancellationToken, Task> work, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var outbox = new Outbox();
            await work(scope.ServiceProvider.GetRequiredService<TamsDbContext>(), outbox, DateTimeOffset.UtcNow, ct);
            await outbox.FlushAsync(hub);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sweep step '{Step}' failed; retrying next tick", step);
        }
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
