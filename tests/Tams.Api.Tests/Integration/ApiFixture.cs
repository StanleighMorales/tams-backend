using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Tams.Api.Tests.Integration;

/// <summary>
/// One real PostgreSQL container and one running API shared by every integration test (the "api"
/// collection runs them serially, so tests that change global settings cannot race).
/// Runs in Development so the app migrates the schema and seeds the bootstrap admin, like local use.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@tests.local";
    public const string AdminPassword = "Admin#Pass123";
    public const string RolePassword = "Role#Pass123";

    readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();
    readonly string _scratch = Path.Combine(Path.GetTempPath(), "tams-tests-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string> _tokens = [];
    WebApplicationFactory<Program> _factory = null!;

    public Dictionary<string, Guid> UserIds { get; } = [];

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:Default", _db.GetConnectionString())
            .UseSetting("Jwt:Key", new string('k', 48))
            .UseSetting("Bootstrap:AdminEmail", AdminEmail)
            .UseSetting("Bootstrap:AdminPassword", AdminPassword)
            .UseSetting("DataProtection:KeysPath", Path.Combine(_scratch, "keys"))
            .UseSetting("Media:Path", Path.Combine(_scratch, "media"))
            .UseSetting("Docs:Enabled", "true"));

        // One user per role; the instructor is also an event coordinator so it is a valid card owner.
        var admin = await As("ADMIN");
        foreach (var (role, extra) in new[] { ("TECH_STAFF", ""), ("CUSTODIAN", ""), ("INSTRUCTOR", ",\"isEventCoordinator\":true") })
        {
            var body = await admin.PostRawAsync("/api/users",
                $$"""{"email":"{{role.ToLowerInvariant()}}@tests.local","password":"{{RolePassword}}","employeeNo":"T-{{role}}","firstName":"{{role}}","lastName":"Test","role":"{{role}}"{{extra}}}""")
                .Ok(201);
            UserIds[role] = body.Id();
        }
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _db.DisposeAsync();
        try { Directory.Delete(_scratch, true); } catch { /* best effort */ }
    }

    /// <summary>A client signed in as the given role (ADMIN, TECH_STAFF, CUSTODIAN, INSTRUCTOR).</summary>
    public async Task<ApiClient> As(string role)
    {
        if (!_tokens.TryGetValue(role, out var token))
        {
            var (email, password) = role == "ADMIN" ? (AdminEmail, AdminPassword) : ($"{role.ToLowerInvariant()}@tests.local", RolePassword);
            var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });
            res.EnsureSuccessStatusCode();
            token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
            _tokens[role] = token;
        }
        return WithToken(token);
    }

    public ApiClient WithToken(string token) => Make(token);

    /// <summary>A client with no credentials (public and kiosk routes).</summary>
    public ApiClient NoAuth() => Make(null);

    ApiClient Make(string? token)
    {
        var http = _factory.CreateClient();
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new ApiClient(http);
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
