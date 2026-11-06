using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Auth;

namespace Tams.Api.Tests;

public class EndpointAuthorizationCoverageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndpointAuthorizationCoverageTests(WebApplicationFactory<Program> factory) =>
        // Dummy connection string: Npgsql never connects because this test only reads the endpoint table.
        // "Testing" (not Development) skips the startup migration; Docs:Enabled maps openapi/scalar anyway.
        _factory = factory.WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Default", "Host=localhost;Database=unused;Username=x;Password=x")
            .UseSetting("Jwt:Key", new string('k', 48)) // TAMS-31 fails fast without a signing key
            .UseSetting("Docs:Enabled", "true"));

    [Fact]
    public void Every_endpoint_is_guarded_or_explicitly_anonymous()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        Assert.NotEmpty(endpoints.Endpoints); // guards against a vacuous pass
        var violations = AuthorizationCoverage.FindViolations(endpoints);

        Assert.True(violations.Count == 0, "Unguarded endpoints:\n" + string.Join('\n', violations));
    }
}
