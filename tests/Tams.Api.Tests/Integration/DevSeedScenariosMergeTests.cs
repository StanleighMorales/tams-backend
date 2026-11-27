using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Testcontainers.PostgreSql;

namespace Tams.Api.Tests.Integration;

/// <summary>Seed:Scenarios on a database that already holds rows with the same unique keys (a hosted dev database). It has its
/// own database so the scenario rows do not leak into the shared fixture, which must keep seeing the small demo set.</summary>
public class DevSeedScenariosMergeTests
{
    [Fact]
    public async Task Scenarios_top_up_a_database_that_already_has_some_of_their_categories()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await pg.StartAsync();

        WebApplicationFactory<Program> Start(bool scenarios) => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:Default", pg.GetConnectionString())
            .UseSetting("Jwt:Key", new string('k', 48))
            .UseSetting("Bootstrap:AdminEmail", ApiFixture.AdminEmail)
            .UseSetting("Bootstrap:AdminPassword", ApiFixture.AdminPassword)
            .UseSetting("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), "tams-merge-keys"))
            .UseSetting("Media:Path", Path.Combine(Path.GetTempPath(), "tams-merge-media"))
            .UseSetting("Seed:Demo", "true")
            .UseSetting("Seed:Scenarios", scenarios ? "true" : "false"));

        // Demo set only, plus a category the scenario set also wants.
        using (var first = Start(false))
        {
            using var scope = first.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
            db.AssetCategories.Add(new AssetCategory { Id = Guid.NewGuid(), Name = "Projectors", Description = "added by hand", IsBorrowable = true });
            await db.SaveChangesAsync();
        }

        // Startup runs the scenario seed against it; a second run must be a no-op.
        using var second = Start(true);
        await second.Services.SeedScenarioDataAsync();

        using var check = second.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<TamsDbContext>();
        Assert.True(await after.Assets.AnyAsync(a => a.AssetCode == "LT-003"));
        Assert.Equal(1, await after.AssetCategories.CountAsync(c => c.Name == "Projectors"));
        Assert.Equal("added by hand", (await after.AssetCategories.SingleAsync(c => c.Name == "Projectors")).Description);
    }
}
