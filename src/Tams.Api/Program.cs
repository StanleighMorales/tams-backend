using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Assets;
using Tams.Api.Auditing;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Facilities;
using Tams.Api.Enrollment;
using Tams.Api.Http;
using Tams.Api.Users;
using Tams.Api.Students;
using Tams.Api.Settings;
using Tams.Api.Qr;
using Tams.Api.Events;
using Tams.Api.Inventory;
using Tams.Api.Kiosk;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Push;
using Tams.Api.Reports;
using Tams.Api.Retention;
using Tams.Api.Violations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTamsOpenApi();
builder.Services.AddTamsProblemDetails();
// Development defaults to throwing on an unparseable body, which the exception handler turns into a 500.
// Production answers 400; make Development match so clients see the same thing everywhere.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
builder.Services.AddTamsAuth(builder.Configuration);
builder.Services.AddTamsPolicies();

// Browser front ends (TanStack Router on Vite) run on another origin, so they need CORS. Cors:Origins is a
// comma-separated list (env var Cors__Origins); Development defaults to the Vite port. Empty means no CORS.
// Auth is a Bearer header, not a cookie, but SignalR's /hubs/live/negotiate is fetched with credentials, so
// the browser needs Access-Control-Allow-Credentials (origins are explicit, never *).
var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (corsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Default is not set. Copy .env.example to .env and set POSTGRES_PASSWORD.");

builder.Services.AddDbContext<TamsDbContext>(o => o.UseNpgsql(connectionString, PgEnums.MapAll));
builder.Services.AddTamsSettings();
builder.Services.AddSingleton<CardEnrollmentDesk>(); // in-memory "Add card" requests: one API instance
builder.Services.AddHostedService<EnrollmentSweeper>();
builder.Services.AddHostedService<LoanSweeper>();
builder.Services.AddHostedService<Tams.Api.Retention.RetentionSweeper>();
builder.Services.AddSignalR();
builder.Services.AddTamsPush(builder.Configuration, builder.Environment);

// Fingerprint templates are encrypted with Data Protection; the key ring must survive container
// rebuilds (Compose mounts the dp-keys volume at /app/keys) or stored templates become unreadable.
builder.Services.AddDataProtection()
    .SetApplicationName("tams")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"] ?? "/app/keys"));

// Only Caddy can reach the api container (Compose network), so its X-Forwarded-* headers are trusted:
// clearing the known networks/proxies lets the middleware accept them from the Docker network address.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders(); // first: auth, audit IPs and kiosk IP checks need the real client address

// PRD 6.3: endpoints live under /api. PathBase strips the prefix, so /api/x and /x both route,
// which keeps the Compose healthcheck and local curl working. UseRouting must come after it.
app.UsePathBase("/api");
app.UseRouting();

app.UseTamsProblemDetails();
if (corsOrigins.Length > 0) app.UseCors(); // before auth so preflight (OPTIONS) requests are not rejected as 401
app.UseTamsAuth();
app.MapTamsDocs();

if (app.Environment.IsDevelopment())
{
    // Development only: bring the schema up to date on start. Production applies
    // migrations deliberately (dotnet ef database update or a migration bundle).
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TamsDbContext>().Database.MigrateAsync();
}

await app.SeedDevelopmentAdminAsync();
await app.Services.SeedDemoDataAsync();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapAssetEndpoints();
app.MapStudentEndpoints();
app.MapEnrollmentEndpoints();
app.MapQrEndpoints();
app.MapFacilityEndpoints();
app.MapSettingsEndpoints();
app.MapAuditLogEndpoints();
app.MapReportEndpoints();
app.MapRetentionEndpoints();
app.MapKioskEndpoints();
app.MapApprovalEndpoints();
app.MapDeviceEndpoints();
app.MapLoanEndpoints();
app.MapBorrowerEndpoints();
app.MapDamageEndpoints();
app.MapViolationEndpoints();
app.MapEventEndpoints();
app.MapInventoryAuditEndpoints();
app.MapNotificationEndpoints();
app.MapDeviceTokenEndpoints();
app.MapHub<LiveHub>("/hubs/live").RequireAuthorization(Policies.AnyUser); // PRD 6.4

// Compose healthcheck polls this, and it is the first thing to check after a deploy.
app.MapGet("/health", async (TamsDbContext db, IConfiguration cfg, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        if (await db.Database.CanConnectAsync(ct))
        {
            // Backup state is informational (HTTP stays 200): a failed backup must not take the API out of rotation.
            return BackupStatus.Read(cfg["Backup:StatusPath"], DateTimeOffset.UtcNow) is { } backup
                ? Results.Ok(new { status = "ok", database = "up", backup })
                : Results.Ok(new { status = "ok", database = "up" });
        }
    }
    catch (Exception ex)
    {
        // The reason goes to the log, not to the response: this endpoint is public.
        logger.LogError(ex, "Health check could not reach PostgreSQL");
    }

    return Results.Json(
        new { status = "degraded", database = "down" },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

// Development only: the interactive kiosk simulator (docs/SIMULATOR.md). Same origin as the API, so no CORS.
if (app.Environment.IsDevelopment())
{
    var simPage = Path.Combine(AppContext.BaseDirectory, "Sim", "kiosk-sim.html");
    app.MapGet("/sim", () => File.Exists(simPage) ? Results.File(simPage, "text/html; charset=utf-8") : Results.NotFound())
        .AllowAnonymous().ExcludeFromDescription();

    // The simulator's Reset button: drop the database, migrate, and seed it again (admin + demo data when Seed:Demo is on).
    // The custom header forces a CORS preflight, so another web page cannot trigger it with a simple cross-site POST.
    // ADMIN only: the dev host is shared (TAMS-133), so an anonymous POST must not be able to wipe it.
    app.MapPost("/sim/reset", async (HttpRequest req) =>
    {
        if (req.Headers["X-Sim-Reset"] != "yes") return Results.BadRequest(new { code = "RESET_HEADER_REQUIRED" });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        await app.SeedDevelopmentAdminAsync();
        await app.Services.SeedDemoDataAsync();
        return Results.Ok(new { reset = true });
    }).RequireAuthorization(Policies.AdminOnly).ExcludeFromDescription();
}

if (app.Environment.IsDevelopment()) AuthorizationCoverage.LogViolations(app); // keep after the last Map* call

app.Run();
