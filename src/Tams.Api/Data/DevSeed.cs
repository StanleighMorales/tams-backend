using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data.Entities;
using Tams.Api.Enrollment;

namespace Tams.Api.Data;

/// <summary>
/// Development only, and only when Seed:Demo is true: fills an empty database with the sample data from
/// docs/TEST-DATA.md so front-end work has something to show. Safe to run on every start: it does nothing
/// once the STORE-1 room exists. Never runs outside Development.
/// </summary>
public static class DevSeed
{
    /// <summary>Password of every demo account (the admin keeps the Bootstrap password).</summary>
    public const string Password = "Demo#Pass123";

    /// <summary>X-Station-Key of the demo room reader ROOM-101 (the simulator sends it). Demo data only.</summary>
    public const string DemoRoomKey = "demo-room-101-key";

    public static async Task SeedDemoDataAsync(this IServiceProvider services)
    {
        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment()
            || !services.GetRequiredService<IConfiguration>().GetValue<bool>("Seed:Demo")) return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var now = DateTimeOffset.UtcNow;

        User NewUser(string email, string employeeNo, string first, string last, UserRole role, bool coordinator = false)
        {
            var u = new User
            {
                Id = Guid.NewGuid(), Email = email, UserName = email,
                NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString(),
                LockoutEnabled = true, EmployeeNo = employeeNo, FirstName = first, LastName = last,
                Role = role, IsEventCoordinator = coordinator, CreatedAt = now, UpdatedAt = now,
            };
            u.PasswordHash = hasher.HashPassword(u, Password);
            return u;
        }

        async Task EnsureCardAsync(User u, string uid)
        {
            if (!await db.AuthCards.AnyAsync(c => c.CardUid == uid))
                db.AuthCards.Add(new AuthCard { Id = Guid.NewGuid(), CardUid = uid, UserId = u.Id, IssuedAt = now });
        }

        // Instructor 3 and the cards of instructors 2 and 3 were added after the first demo set shipped,
        // so they also top up databases that were already seeded.
        var instructor3 = await db.Users.FirstOrDefaultAsync(u => u.Email == "instructor3@tams.test");
        if (instructor3 is null) { instructor3 = NewUser("instructor3@tams.test", "E-1006", "Iris", "Instructor", UserRole.Instructor); db.Users.Add(instructor3); }
        if (await db.Rooms.AnyAsync(r => r.Code == "STORE-1"))
        {
            await EnsureCardAsync(await db.Users.FirstAsync(u => u.Email == "instructor2@tams.test"), "04A1B2C4");
            await EnsureCardAsync(instructor3, "04A1B2C5");
            await db.SaveChangesAsync();
            return;
        }

        var tech = NewUser("tech@tams.test", "E-1002", "Tess", "Technician", UserRole.TechStaff);
        var custodian = NewUser("custodian@tams.test", "E-1003", "Joy", "Custodian", UserRole.Custodian);
        var instructor = NewUser("instructor@tams.test", "E-1004", "Ina", "Instructor", UserRole.Instructor, coordinator: true);
        var instructor2 = NewUser("instructor2@tams.test", "E-1005", "Ian", "Instructor", UserRole.Instructor);
        db.Users.AddRange(tech, custodian, instructor, instructor2);

        Room NewRoom(string code, string name, short floor, bool storage) => new()
        {
            Id = Guid.NewGuid(), Code = code, Name = name, Building = "Main", Floor = floor, IsStorage = storage,
            CreatedAt = now, UpdatedAt = now,
        };
        var store = NewRoom("STORE-1", "Equipment Storage", 1, true);
        var lab1 = NewRoom("LAB-101", "Computer Lab 1", 1, false);
        var lab2 = NewRoom("LAB-102", "Computer Lab 2", 2, false);
        db.Rooms.AddRange(store, lab1, lab2);

        db.Stations.AddRange(
            new Station { Id = Guid.NewGuid(), StationCode = "KIOSK-1", Type = StationType.CounterKiosk, RoomId = store.Id, Name = "Counter Kiosk", CreatedAt = now, UpdatedAt = now },
            new Station { Id = Guid.NewGuid(), StationCode = "ROOM-101", Type = StationType.RoomStation, RoomId = lab1.Id, Name = "Lab 1 Reader", KeyHash = StationAuth.HashKey(DemoRoomKey), CreatedAt = now, UpdatedAt = now });

        db.AuthCards.Add(new AuthCard { Id = Guid.NewGuid(), CardUid = "04A1B2C3", UserId = instructor.Id, IssuedAt = now });
        await EnsureCardAsync(instructor2, "04A1B2C4");
        await EnsureCardAsync(instructor3, "04A1B2C5");

        var laptops = new AssetCategory { Id = Guid.NewGuid(), Name = "Laptops", Description = "Borrowable laptops", IsBorrowable = true };
        var desktops = new AssetCategory { Id = Guid.NewGuid(), Name = "Desktop PCs", Description = "Fixed lab PCs", IsBorrowable = false };
        db.AssetCategories.AddRange(laptops, desktops);

        Asset NewAsset(string code, string name, AssetCategory cat, AssetType type, string brand, string model, string? rfid, Room home, decimal cost) => new()
        {
            Id = Guid.NewGuid(), AssetCode = code, Name = name, CategoryId = cat.Id, Type = type, Brand = brand, Model = model,
            SerialNo = "SN-" + code, RfidTagUid = rfid, HomeRoomId = home.Id, CurrentRoomId = home.Id,
            ReplacementCost = cost, AcquiredAt = new DateOnly(2026, 6, 15), CreatedAt = now, UpdatedAt = now,
        };
        db.Assets.AddRange(
            NewAsset("LT-001", "Dell Latitude 5440", laptops, AssetType.Borrowable, "Dell", "Latitude 5440", "E2000017221101441890", store, 45000m),
            NewAsset("LT-002", "Lenovo ThinkPad E14", laptops, AssetType.Borrowable, "Lenovo", "E14", "04A1B2C3D4E5F6", store, 38000m),
            NewAsset("PC-101-01", "Lab 1 Desktop 01", desktops, AssetType.Fixed, "HP", "ProDesk 400", null, lab1, 32000m));

        var bscs = new Section { Id = Guid.NewGuid(), Code = "BSCS-3A", Program = "BSCS", YearLevel = 3, SchoolYear = "2026-2027", AdviserUserId = instructor.Id };
        var bsit = new Section { Id = Guid.NewGuid(), Code = "BSIT-1B", Program = "BSIT", YearLevel = 1, SchoolYear = "2026-2027" };
        db.Sections.AddRange(bscs, bsit);

        Student NewStudent(string no, string first, string last, Section s) => new()
        {
            Id = Guid.NewGuid(), StudentNo = no, FirstName = first, LastName = last, SectionId = s.Id, CreatedAt = now, UpdatedAt = now,
        };
        db.Students.AddRange(
            NewStudent("2024-0001", "Juan", "Dela Cruz", bscs),
            NewStudent("2024-0002", "Maria", "Santos, Jr.", bscs),
            NewStudent("2024-0003", "Ana", "Reyes", bsit),
            NewStudent("2024-0004", "Pedro", "Garcia", bscs));

        await db.SaveChangesAsync();
        services.GetRequiredService<ILoggerFactory>().CreateLogger("DevSeed")
            .LogWarning("Seed:Demo loaded sample data. Demo accounts tech@/custodian@/instructor@/instructor2@tams.test use password {Password}", Password);
    }
}
