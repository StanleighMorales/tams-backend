using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Assets;
using Tams.Api.Data;
using Tams.Api.Facilities;
using Tams.Api.Enrollment;
using Tams.Api.Http;
using Tams.Api.Users;
using Tams.Api.Students;
using Tams.Api.Settings;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTamsOpenApi();
builder.Services.AddTamsProblemDetails();
builder.Services.AddTamsAuth(builder.Configuration);
builder.Services.AddTamsPolicies();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Default is not set. Copy .env.example to .env and set POSTGRES_PASSWORD.");

builder.Services.AddDbContext<TamsDbContext>(o => o.UseNpgsql(connectionString, PgEnums.MapAll));
builder.Services.AddTamsSettings();

var app = builder.Build();

app.UseTamsProblemDetails();
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
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapAssetEndpoints();
app.MapStudentEndpoints();
app.MapEnrollmentEndpoints();

// Compose healthcheck polls this, and it is the first thing to check after a deploy.
app.MapGet("/health", async (TamsDbContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        if (await db.Database.CanConnectAsync(ct))
            return Results.Ok(new { status = "ok", database = "up" });
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

if (app.Environment.IsDevelopment()) AuthorizationCoverage.LogViolations(app); // keep after the last Map* call

app.MapFacilityEndpoints();

app.MapSettingsEndpoints();

app.Run();
