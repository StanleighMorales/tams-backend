using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Tams.Api.Http;

public static class OpenApiSetup
{
    public static IServiceCollection AddTamsOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "TAMS API";
            doc.Info.Version = "v1";
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT access token: Authorization: Bearer <token>",
            };
            // Applies to every operation; anonymous endpoints (/health, login) just ignore it.
            doc.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] },
            ];
            return Task.CompletedTask;
        }));

    /// <summary>Spec at /openapi/v1.json, UI at /scalar. Development, or Docs:Enabled=true.</summary>
    public static WebApplication MapTamsDocs(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Docs:Enabled"))
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference("/scalar").AllowAnonymous();
        }
        return app;
    }
}
