namespace Tams.Api.Http;

/// <summary>
/// Standard error format: RFC 7807 problem details plus a stable machine-readable
/// <c>code</c> and the <c>traceId</c>. Endpoints return these helpers; the framework
/// fills in the same shape for unhandled exceptions and bare 401/403/404 responses.
/// </summary>
public static class Problems
{
    public static IResult BadRequest(string code, string detail) => Make(400, code, detail);
    public static IResult Unauthorized(string code, string detail) => Make(401, code, detail);
    public static IResult Forbidden(string code, string detail) => Make(403, code, detail);
    public static IResult NotFound(string code, string detail) => Make(404, code, detail);
    public static IResult Conflict(string code, string detail) => Make(409, code, detail);

    /// <summary>409 with extra fields next to <c>code</c> (for example the flag details of a refused borrower).</summary>
    public static IResult Conflict(string code, string detail, IDictionary<string, object?> extra) =>
        Results.Problem(statusCode: 409, detail: detail,
            extensions: new Dictionary<string, object?>(extra) { ["code"] = code });

    /// <summary>400 with field errors keyed by field name; code is VALIDATION_FAILED.</summary>
    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_FAILED" });

    private static IResult Make(int status, string code, string detail) =>
        Results.Problem(statusCode: status, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    // Fallback codes for errors that did not come through the helpers above.
    private static string DefaultCode(int status) => status switch
    {
        400 => "BAD_REQUEST",
        401 => "UNAUTHORIZED",
        403 => "FORBIDDEN",
        404 => "NOT_FOUND",
        405 => "METHOD_NOT_ALLOWED",
        409 => "CONFLICT",
        415 => "UNSUPPORTED_MEDIA_TYPE",
        >= 500 => "INTERNAL_ERROR",
        _ => "ERROR",
    };

    public static IServiceCollection AddTamsProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            var pd = ctx.ProblemDetails;
            pd.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;
            pd.Extensions.TryAdd("code", pd is HttpValidationProblemDetails
                ? "VALIDATION_FAILED"
                : DefaultCode(pd.Status ?? ctx.HttpContext.Response.StatusCode));

            // Never leak exception text outside Development.
            if (pd.Status >= 500 && !ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
            {
                pd.Detail = null;
            }
        });

    /// <summary>Exception handler and status-code pages, both emitting problem details.</summary>
    public static WebApplication UseTamsProblemDetails(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
