using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Facilities;
using Tams.Api.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTamsOpenApi();
builder.Services.AddTamsProblemDetails();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Default is not set. Copy .env.example to .env and set POSTGRES_PASSWORD.");

builder.Services.AddDbContext<TamsDbContext>(o => o.UseNpgsql(connectionString, PgEnums.MapAll));

var app = builder.Build();

app.UseTamsProblemDetails();
app.MapTamsDocs();

if (app.Environment.IsDevelopment())
{
    // Development only: bring the schema up to date on start. Production applies
    // migrations deliberately (dotnet ef database update or a migration bundle).
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TamsDbContext>().Database.MigrateAsync();
}

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
});

app.MapFacilityEndpoints();

app.Run();
