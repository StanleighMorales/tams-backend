using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace Tams.Api.Auth;

// Every endpoint must carry explicit authorization metadata. The FallbackPolicy already denies
// anonymous callers at runtime; this guard makes a forgotten guard visible at start time and in tests.
public static class AuthorizationCoverage
{
    public static IReadOnlyList<string> FindViolations(EndpointDataSource endpoints)
    {
        var violations = new List<string>();
        foreach (var e in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var route = "/" + (e.RoutePattern.RawText ?? "").TrimStart('/');
            if (route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) route = route[4..];
            var anonymousAllowed = Policies.AnonymousRoutes.Any(a =>
                route.Equals(a, StringComparison.OrdinalIgnoreCase) ||
                route.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase));

            var allowsAnon = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var guarded = e.Metadata.GetMetadata<IAuthorizeData>() is not null;

            if (allowsAnon && !anonymousAllowed)
                violations.Add($"{e.DisplayName}: {route} is AllowAnonymous but not in Policies.AnonymousRoutes");
            else if (!allowsAnon && !guarded)
                violations.Add($"{e.DisplayName}: {route} has no authorization (add RequireAuthorization/RequireRoles)");
        }
        return violations;
    }

    public static void LogViolations(WebApplication app)
    {
        var violations = FindViolations(app.Services.GetRequiredService<EndpointDataSource>());
        if (violations.Count == 0) return;
        app.Logger.LogError("Authorization coverage: {Count} unguarded endpoint(s):\n{List}",
            violations.Count, string.Join('\n', violations));
    }
}
