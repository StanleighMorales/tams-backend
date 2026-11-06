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

        g.MapGet("", async (TamsDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AssetCategories.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new CategoryDto(c.Id, c.Name, c.Description, c.IsBorrowable,
                    db.Assets.Count(a => a.CategoryId == c.Id && a.DeletedAt == null)))
                .ToListAsync(ct)))
            .RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

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
            AssetAudit.Add(db, http, "CATEGORY_CREATED", "Category", cat.Id, null, cat);
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
            AssetAudit.Add(db, http, "CATEGORY_UPDATED", "Category", cat.Id, before,
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
            AssetAudit.Add(db, http, "CATEGORY_DELETED", "Category", cat.Id, cat, null);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireRoles(Roles.Admin, Roles.TechStaff);
    }

    static IResult? Validate(CategoryRequest req)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.Name)) errors["name"] = ["Name is required."];
        else if (req.Name.Trim().Length > 80) errors["name"] = ["Name must be 80 characters or fewer."];
        return errors.Count > 0 ? Problems.Validation(errors) : null;
    }
}
