using Npgsql;
using Tams.Api.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTamsOpenApi();
builder.Services.AddTamsProblemDetails();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Default is not set. Copy .env.example to .env and set POSTGRES_PASSWORD.");

// ponytail: raw Npgsql data source, no EF Core yet. TAMS-27 adds DbContext and migrations.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

var app = builder.Build();

app.UseTamsProblemDetails();
app.MapTamsDocs();

// Compose healthcheck polls this, and it is the first thing to check after a deploy.
app.MapGet("/health", async (NpgsqlDataSource db, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        await using var cmd = db.CreateCommand("select 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ok", database = "up" });
    }
    catch (NpgsqlException ex)
    {
        // The reason goes to the log, not to the response: this endpoint is public.
        logger.LogError(ex, "Health check could not reach PostgreSQL");
        return Results.Json(
            new { status = "degraded", database = "down" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
