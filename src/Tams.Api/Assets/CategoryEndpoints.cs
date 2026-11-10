using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Assets;

static class CategoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/asset-categories").WithTags("Asset categories");

        g.MapGet("", List).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

        g.MapGet("{id:guid}", async (Guid id, TamsDbContext db, CancellationToken ct) =>
            await db.AssetCategories.AsNoTracking().Where(c => c.Id == id)
                .Select(c => new CategoryDto(c.Id, c.Name, c.Description, c.IsBorrowable,
                    db.Assets.Count(a => a.CategoryId == c.Id && a.DeletedAt == null)))
                .FirstOrDefaultAsync(ct) is { } dto
                ? Results.Ok(dto)
                : Problems.NotFound("CATEGORY_NOT_FOUND", "Category not found."))
            .RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

        g.MapPost("", async (CategoryRequest req, TamsDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (Validate(req) is { } bad) return bad;
            var name = req.Name!.Trim();
            if (await db.AssetCategories.AnyAsync(c => c.Name.ToLower() == name.ToLower(), ct))
                return Problems.Conflict("CATEGORY_NAME_TAKEN", $"A category named '{name}' already exists.");

            var cat = new AssetCategory
            {
                Id = Guid.NewGuid(), Name = name, Description = req.Description?.Trim(), IsBorrowable = req.IsBorrowable ?? false,
            };
            db.AssetCategories.Add(cat);
            AssetAudit.Add(db, http, "CATEGORY_CREATED", "asset_category", cat.Id, null, cat);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (AssetAudit.IsUniqueViolation(ex))
            {
                return Problems.Conflict("CATEGORY_NAME_TAKEN", $"A category named '{name}' already exists.");
            }
            return Results.Created($"/asset-categories/{cat.Id}",
                new CategoryDto(cat.Id, cat.Name, cat.Description, cat.IsBorrowable, 0));
        }).RequireRoles(Roles.Admin, Roles.TechStaff);

        g.MapPut("{id:guid}", async (Guid id, CategoryRequest req, TamsDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (Validate(req) is { } bad) return bad;
            var cat = await db.AssetCategories.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cat is null) return Problems.NotFound("CATEGORY_NOT_FOUND", "Category not found.");

            var name = req.Name!.Trim();
            if (await db.AssetCategories.AnyAsync(c => c.Id != id && c.Name.ToLower() == name.ToLower(), ct))
                return Problems.Conflict("CATEGORY_NAME_TAKEN", $"A category named '{name}' already exists.");

            var before = new { cat.Name, cat.Description, cat.IsBorrowable };
            cat.Name = name;
            cat.Description = req.Description?.Trim();
            cat.IsBorrowable = req.IsBorrowable ?? cat.IsBorrowable;
            AssetAudit.Add(db, http, "CATEGORY_UPDATED", "asset_category", cat.Id, before,
                new { cat.Name, cat.Description, cat.IsBorrowable });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (AssetAudit.IsUniqueViolation(ex))
            {
                return Problems.Conflict("CATEGORY_NAME_TAKEN", $"A category named '{name}' already exists.");
            }
            var count = await db.Assets.CountAsync(a => a.CategoryId == id && a.DeletedAt == null, ct);
            return Results.Ok(new CategoryDto(cat.Id, cat.Name, cat.Description, cat.IsBorrowable, count));
        }).RequireRoles(Roles.Admin, Roles.TechStaff);

        // Categories have no deleted_at, so this is a hard delete and is refused while any asset
        // (including soft-deleted ones, which can be restored) still points at the category.
        g.MapDelete("{id:guid}", async (Guid id, TamsDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var cat = await db.AssetCategories.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cat is null) return Problems.NotFound("CATEGORY_NOT_FOUND", "Category not found.");
            if (await db.Assets.AnyAsync(a => a.CategoryId == id, ct))
                return Problems.Conflict("CATEGORY_IN_USE", "Category still has assets (deleted assets count). Move or remove them first.");

            db.AssetCategories.Remove(cat);
            AssetAudit.Add(db, http, "CATEGORY_DELETED", "asset_category", cat.Id, cat, null);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireRoles(Roles.Admin, Roles.TechStaff);
    }

    static async Task<IResult> List(TamsDbContext db, CancellationToken ct,
        string? q, string? sort, int page = 1, int pageSize = 25)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors["page"] = ["Must be 1 or greater."];
        if (pageSize is < 1 or > 100) errors["pageSize"] = ["Must be between 1 and 100."];
        var sortKey = sort?.TrimStart('-').ToLowerInvariant();
        if (sortKey is not (null or "" or "name")) errors["sort"] = ["Must be name, optionally prefixed with '-'."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var query = db.AssetCategories.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(c => EF.Functions.ILike(c.Name, like)
                || (c.Description != null && EF.Functions.ILike(c.Description, like)));
        }
        var total = await query.CountAsync(ct);
        query = sort?.StartsWith('-') == true ? query.OrderByDescending(c => c.Name) : query.OrderBy(c => c.Name);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Description, c.IsBorrowable,
                db.Assets.Count(a => a.CategoryId == c.Id && a.DeletedAt == null)))
            .ToListAsync(ct);
        return Results.Ok(new PagedResult<CategoryDto>(items, page, pageSize, total));
    }

    static IResult? Validate(CategoryRequest req)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.Name)) errors["name"] = ["Name is required."];
        else if (req.Name.Trim().Length > 80) errors["name"] = ["Name must be 80 characters or fewer."];
        return errors.Count > 0 ? Problems.Validation(errors) : null;
    }
}
