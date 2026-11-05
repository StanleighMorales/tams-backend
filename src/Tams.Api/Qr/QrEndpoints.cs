using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Qr;

public static class QrEndpoints
{
    public const int MaxSheetAssets = 200;
    private const int DefaultSize = 512, MinSize = 64, MaxSize = 2048;

    public sealed record QrSheetRequest(List<Guid>? AssetIds, Guid? CategoryId);

    public static IEndpointRouteBuilder MapQrEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/assets").WithTags("QR").RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

        g.MapGet("/{id:guid}/qr", GetQr)
            .Produces(200, contentType: "image/png")
            .Produces(200, contentType: "image/svg+xml");

        g.MapPost("/qr-sheet", CreateSheet)
            .Produces(200, contentType: "application/pdf");

        return app;
    }

    private static async Task<IResult> GetQr(Guid id, string? format, int? size, TamsDbContext db, CancellationToken ct)
    {
        var svg = false;
        if (format is not null)
        {
            if (format.Equals("svg", StringComparison.OrdinalIgnoreCase)) svg = true;
            else if (!format.Equals("png", StringComparison.OrdinalIgnoreCase))
                return Problems.BadRequest("INVALID_QR_FORMAT", "format must be png or svg.");
        }
        var px = size ?? DefaultSize;
        if (px is < MinSize or > MaxSize)
            return Problems.BadRequest("INVALID_QR_SIZE", $"size must be between {MinSize} and {MaxSize} pixels.");

        if (!await db.Assets.AnyAsync(a => a.Id == id && a.DeletedAt == null, ct))
            return Problems.NotFound("ASSET_NOT_FOUND", "Asset not found.");

        // The QR depends only on the immutable asset id, so a long private cache and a stable ETag are safe.
        var etag = new EntityTagHeaderValue($"\"{id:N}-{(svg ? "svg" : "png")}-{px}\"");
        return svg
            ? new CachedResult(Results.Text(QrCodes.Svg(id, px), "image/svg+xml"), etag)
            : new CachedResult(Results.File(QrCodes.Png(id, px), "image/png"), etag);
    }

    private static async Task<IResult> CreateSheet(
        [FromBody] QrSheetRequest? req, TamsDbContext db, IConfiguration config, ClaimsPrincipal user,
        HttpContext http, ILogger<QrSheetRequest> logger, CancellationToken ct)
    {
        if (req is null || (req.AssetIds is null) == (req.CategoryId is null))
            return Problems.Validation(new Dictionary<string, string[]>
            {
                ["body"] = ["Provide exactly one of assetIds or categoryId."],
            });

        var query = db.Assets.AsNoTracking().Where(a => a.DeletedAt == null);
        List<Guid>? requested = null;
        if (req.AssetIds is not null)
        {
            requested = req.AssetIds.Distinct().ToList();
            if (requested.Count == 0)
                return Problems.Validation(new Dictionary<string, string[]> { ["assetIds"] = ["At least one asset id is required."] });
            if (requested.Count > MaxSheetAssets)
                return Problems.BadRequest("QR_SHEET_TOO_LARGE", $"A sheet holds at most {MaxSheetAssets} labels.");
            query = query.Where(a => requested.Contains(a.Id));
        }
        else
        {
            query = query.Where(a => a.CategoryId == req.CategoryId);
        }

        // Take one past the cap so an over-large category is detected without loading it all.
        var assets = await query.OrderBy(a => a.AssetCode)
            .Select(a => new { a.Id, a.AssetCode, a.Name })
            .Take(MaxSheetAssets + 1).ToListAsync(ct);

        if (assets.Count > MaxSheetAssets)
            return Problems.BadRequest("QR_SHEET_TOO_LARGE", $"A sheet holds at most {MaxSheetAssets} labels; narrow the filter.");
        if (requested is not null)
        {
            var missing = requested.Except(assets.Select(a => a.Id)).ToList();
            if (missing.Count > 0)
                return Problems.NotFound("ASSET_NOT_FOUND", $"Asset not found: {string.Join(", ", missing)}.");
        }
        if (assets.Count == 0)
            return Problems.NotFound("ASSET_NOT_FOUND", "No assets match the request.");

        var pdf = QrSheetPdf.Render(assets.Select(a => (a.Id, a.AssetCode, a.Name)).ToList());

        var mediaId = Guid.NewGuid();
        var relative = $"qr-sheets/{mediaId:N}.pdf";
        var full = Path.Combine(config["Media:Path"] ?? "/app/media", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, pdf, ct);

        var now = DateTimeOffset.UtcNow;
        db.MediaFiles.Add(new MediaFile
        {
            Id = mediaId,
            Kind = MediaKind.QrSheet,
            StoragePath = relative,
            MimeType = "application/pdf",
            SizeBytes = pdf.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(pdf)),
            CreatedAt = now,
        });
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = Guid.TryParse(sub, out var uid) ? uid : null,
            Action = "QR_SHEET_GENERATED",
            EntityType = "media_file",
            EntityId = mediaId,
            After = JsonSerializer.Serialize(new { count = assets.Count, categoryId = req.CategoryId, assetIds = assets.Select(a => a.Id) }),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Do not leave an orphan file behind when the rows did not commit.
            try { File.Delete(full); } catch (IOException ex) { logger.LogWarning(ex, "Could not remove orphan QR sheet {Path}", full); }
            throw;
        }

        http.Response.Headers["X-Media-File-Id"] = mediaId.ToString();
        return Results.File(pdf, "application/pdf", $"qr-sheet-{now:yyyyMMdd-HHmmss}.pdf");
    }

    /// <summary>Adds private cache headers and an ETag, and answers 304 on a matching If-None-Match.</summary>
    private sealed class CachedResult(IResult inner, EntityTagHeaderValue etag) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            var h = ctx.Response.GetTypedHeaders();
            h.CacheControl = new CacheControlHeaderValue { Private = true, MaxAge = TimeSpan.FromDays(1) };
            h.ETag = etag;
            if (ctx.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
            {
                ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                return Task.CompletedTask;
            }
            return inner.ExecuteAsync(ctx);
        }
    }
}
