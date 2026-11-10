using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Assets;

// schema-v1-decisions #5. Files live under Media:Path (default /app/media, the compose volume),
// media_files.storage_path is relative to it.
static class AssetImageEndpoints
{
    const string DefaultRoot = "/app/media";
    const long DefaultMaxBytes = 5 * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/assets/{id:guid}/image").WithTags("Assets");

        g.MapPost("", Upload).DisableAntiforgery().Accepts<IFormFile>("multipart/form-data")
            .RequireRoles(Roles.Admin, Roles.TechStaff);
        g.MapGet("", Download).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);
        g.MapDelete("", Remove).RequireRoles(Roles.Admin, Roles.TechStaff);
    }

    static string Root(IConfiguration cfg) => cfg["Media:Path"] is { Length: > 0 } p ? p : DefaultRoot;

    static async Task<IResult> Upload(
        Guid id, IFormFile? file, TamsDbContext db, IConfiguration cfg, HttpContext http, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (asset is null) return Problems.NotFound("ASSET_NOT_FOUND", "Asset not found.");
        if (file is null || file.Length == 0)
            return Problems.BadRequest("IMAGE_REQUIRED", "Send the image as multipart/form-data in a 'file' field.");

        var max = cfg.GetValue("Media:MaxAssetImageBytes", DefaultMaxBytes);
        if (file.Length > max)
            return Results.Problem(statusCode: 413, detail: $"Image is larger than {max / 1024 / 1024} MB.",
                extensions: new Dictionary<string, object?> { ["code"] = "IMAGE_TOO_LARGE" });

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        {
            bytes = new byte[file.Length];
            await stream.ReadExactlyAsync(bytes, ct);
        }

        // Trust the bytes, not the client-supplied content type.
        string mime, ext;
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) (mime, ext) = ("image/jpeg", "jpg");
        else if (bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            (mime, ext) = ("image/png", "png");
        else return Problems.BadRequest("IMAGE_TYPE_UNSUPPORTED", "Only JPEG and PNG images are accepted.");

        var media = new MediaFile
        {
            Id = Guid.NewGuid(),
            Kind = MediaKind.AssetImage,
            MimeType = mime,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        media.StoragePath = $"asset-images/{media.Id:N}.{ext}";

        var root = Root(cfg);
        var path = Path.Combine(root, media.StoragePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);

        MediaFile? old = asset.ImageFileId is { } oldId ? await db.MediaFiles.FindAsync([oldId], ct) : null;
        try
        {
            db.MediaFiles.Add(media);
            asset.ImageFileId = media.Id;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            if (old is not null) db.MediaFiles.Remove(old);
            AssetAudit.Add(db, http, "ASSET_IMAGE_SET", "asset", asset.Id,
                new { imageFileId = old?.Id }, new { imageFileId = media.Id, media.MimeType, media.SizeBytes, media.Sha256 });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        if (old is not null) TryDelete(Path.Combine(root, old.StoragePath));
        return Results.Ok(new { imageFileId = media.Id, mimeType = media.MimeType, sizeBytes = media.SizeBytes, sha256 = media.Sha256 });
    }

    static async Task<IResult> Download(Guid id, TamsDbContext db, IConfiguration cfg, CancellationToken ct)
    {
        var row = await db.Assets.AsNoTracking().Where(a => a.Id == id && a.DeletedAt == null && a.ImageFileId != null)
            .Join(db.MediaFiles, a => a.ImageFileId, m => m.Id, (_, m) => m).FirstOrDefaultAsync(ct);
        if (row is null) return Problems.NotFound("ASSET_IMAGE_NOT_FOUND", "Asset or image not found.");

        var path = Path.Combine(Root(cfg), row.StoragePath);
        if (!File.Exists(path)) return Problems.NotFound("ASSET_IMAGE_NOT_FOUND", "Image file is missing from storage.");
        return Results.File(path, row.MimeType, enableRangeProcessing: false);
    }

    static async Task<IResult> Remove(Guid id, TamsDbContext db, IConfiguration cfg, HttpContext http, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (asset is null) return Problems.NotFound("ASSET_NOT_FOUND", "Asset not found.");
        if (asset.ImageFileId is not { } imageId) return Results.NoContent();

        var media = await db.MediaFiles.FindAsync([imageId], ct);
        asset.ImageFileId = null;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        if (media is not null) db.MediaFiles.Remove(media);
        AssetAudit.Add(db, http, "ASSET_IMAGE_REMOVED", "asset", asset.Id, new { imageFileId = imageId }, null);
        await db.SaveChangesAsync(ct);
        if (media is not null) TryDelete(Path.Combine(Root(cfg), media.StoragePath));
        return Results.NoContent();
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* orphan file is harmless */ }
    }
}

public static class AssetEndpointsExtensions
{
    /// <summary>Asset categories, assets (CRUD, restore) and asset photos. TAMS-38.</summary>
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        CategoryEndpoints.Map(app);
        AssetEndpoints.Map(app);
        AssetImageEndpoints.Map(app);
        return app;
    }
}
