using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Http;

namespace Tams.Api.Retention;

public sealed record PurgeRequest(string? Confirm);

/// <summary>Admin-only end-of-project deletion (G5, biometric letter section 5). See docs/DATA-RETENTION.md.</summary>
static class RetentionEndpoints
{
    const string ConfirmWord = "DELETE";

    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/data-retention").WithTags("Data retention");
        g.MapPost("/purge-biometrics", PurgeBiometrics).RequireRoles(Roles.Admin);
        g.MapPost("/purge-checkout-photos", PurgePhotos).RequireRoles(Roles.Admin);
        g.MapGet("/purges/{id:guid}/summary", Summary).RequireRoles(Roles.Admin);
        return app;
    }

    static IResult? NotConfirmed(PurgeRequest? req) => req?.Confirm == ConfirmWord ? null
        : Problems.BadRequest("PURGE_NOT_CONFIRMED", $"Send {{\"confirm\":\"{ConfirmWord}\"}} to run this irreversible deletion.");

    static async Task<IResult> PurgeBiometrics(PurgeRequest? req, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (NotConfirmed(req) is { } no) return no;
        var s = await RetentionPurge.PurgeBiometricsAsync(db, AssetAudit.ActorId(http.User), http.Connection.RemoteIpAddress?.ToString(), ct);
        return Results.Ok(new { s.PurgeId, s.Kind, s.PurgedAt, s.PurgedByUserId, s.Counts, summaryUrl = $"/api/data-retention/purges/{s.PurgeId}/summary" });
    }

    static async Task<IResult> PurgePhotos(PurgeRequest? req, HttpContext http, TamsDbContext db, IConfiguration cfg, CancellationToken ct)
    {
        if (NotConfirmed(req) is { } no) return no;
        var s = await RetentionPurge.PurgeCheckoutPhotosAsync(db, cfg, AssetAudit.ActorId(http.User), http.Connection.RemoteIpAddress?.ToString(), ct);
        return Results.Ok(new { s.PurgeId, s.Kind, s.PurgedAt, s.PurgedByUserId, s.Counts, summaryUrl = $"/api/data-retention/purges/{s.PurgeId}/summary" });
    }

    /// <summary>Printable evidence page, rebuilt from the audit_log row so it cannot drift from the record.</summary>
    static async Task<IResult> Summary(Guid id, TamsDbContext db, CancellationToken ct)
    {
        var e = await db.AuditLog.AsNoTracking().FirstOrDefaultAsync(x => x.EntityType == RetentionPurge.EntityType && x.EntityId == id, ct);
        if (e is null) return Problems.NotFound("PURGE_NOT_FOUND", "No such purge.");

        var actor = e.ActorUserId is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == e.ActorUserId)
            .Select(u => u.FirstName + " " + u.LastName + " (" + u.Email + ")").FirstOrDefaultAsync(ct);
        var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(e.After ?? "{}") ?? [];
        var title = e.Action == RetentionPurge.BiometricsAction ? "Fingerprint templates deleted" : "Checkout photos deleted";

        string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>TAMS data deletion record</title>")
          .Append("<style>body{font-family:sans-serif;max-width:40rem;margin:2rem auto}table{border-collapse:collapse;width:100%}td{border:1px solid #888;padding:.4rem}</style></head><body>")
          .Append("<h1>TAMS data deletion record</h1><h2>").Append(H(title)).Append("</h2>")
          .Append("<p>Record ").Append(H(e.EntityId.ToString())).Append("<br>Deleted at ").Append(H(e.CreatedAt.ToString("u")))
          .Append("<br>Run by ").Append(H(actor ?? "unknown")).Append("</p><table>");
        foreach (var (k, v) in counts) sb.Append("<tr><td>").Append(H(k)).Append("</td><td>").Append(v).Append("</td></tr>");
        sb.Append("</table><p>Ledger, loan and audit-log rows were kept. Encrypted backups taken before this deletion still contain the data ")
          .Append("until they expire (14 days after creation, docs/RUNBOOK.md).</p></body></html>");
        return Results.Content(sb.ToString(), "text/html");
    }
}
