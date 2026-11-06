using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Tams.Api.Http;

public static class OpenApiSetup
{
    public static IServiceCollection AddTamsOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(o =>
        {
            o.AddDocumentTransformer(DocumentInfo);
            o.AddOperationTransformer(ProblemResponses);
        });

    // RFC 7807 body shared by every error: `code` is the stable machine-readable value, `traceId` the request id.
    static OpenApiSchema ProblemSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["title"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
            ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Stable error code, e.g. VALIDATION_FAILED." },
            ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Field errors on 400 VALIDATION_FAILED, keyed by field name.",
                AdditionalProperties = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            },
        },
    };

    static Task ProblemResponses(OpenApiOperation op, OpenApiOperationTransformerContext ctx, CancellationToken _)
    {
        var meta = ctx.Description.ActionDescriptor.EndpointMetadata;
        var anonymous = meta.OfType<IAllowAnonymous>().Any();
        var secured = !anonymous; // the fallback policy requires a signed-in user everywhere else
        var restricted = meta.OfType<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Roles) || !string.IsNullOrEmpty(a.Policy))
            || meta.OfType<AuthorizationPolicy>().Any(p => p.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>().Any());
        var hasBody = op.RequestBody is not null || ctx.Description.ParameterDescriptions.Any(p => p.Source.Id is "Body" or "Form");

        var codes = new List<string>();
        if (hasBody) codes.Add("400");
        if (secured) codes.Add("401");
        if (secured && restricted) codes.Add("403");
        if (ctx.Description.RelativePath?.Contains('{') == true) codes.Add("404");

        op.Responses ??= new OpenApiResponses();
        foreach (var code in codes.Where(c => !op.Responses.ContainsKey(c)))
        {
            op.Responses[code] = new OpenApiResponse
            {
                Description = code switch { "400" => "Validation failed", "401" => "Not authenticated", "403" => "Role not allowed", _ => "Not found" },
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference("ProblemDetails", ctx.Document) },
                },
            };
        }
        return Task.CompletedTask;
    }

    static Task DocumentInfo(OpenApiDocument doc, OpenApiDocumentTransformerContext _, CancellationToken __)
    {
            doc.Components ??= new OpenApiComponents();
            doc.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
            doc.Components.Schemas["ProblemDetails"] = ProblemSchema();
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
    }

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
