using Tams.Api.Data;

namespace Tams.Api.Enrollment;

/// <summary>
/// Every 30 s expires overdue enrollment sessions (same code path as the lazy check on access),
/// so an abandoned session frees its station and its stored template is deleted promptly.
/// </summary>
public sealed class EnrollmentSweeper(IServiceScopeFactory scopes, ILogger<EnrollmentSweeper> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval); // first run after one interval, not at startup
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await EnrollmentSessions.ExpireStaleAsync(scope.ServiceProvider.GetRequiredService<TamsDbContext>(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Enrollment session sweep failed; will retry in {Seconds}s", Interval.TotalSeconds);
            }
        }
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
