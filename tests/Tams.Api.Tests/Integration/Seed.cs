using System.Security.Cryptography;
using System.Text;

namespace Tams.Api.Tests.Integration;

/// <summary>
/// Creates the rows a test needs, with unique codes so tests never collide in the shared database.
/// Everything goes through the public API as ADMIN, so this also exercises the create endpoints.
/// </summary>
public static class Seed
{
    public const string SchoolYear = "2026-2027";
    public const string Password = "Pass#Word123";

    public static string Tag() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
    public static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>A throwaway user, so tests that lock, deactivate or re-password never touch the shared role users.</summary>
    public static async Task<(Guid Id, string Email, string Password)> User(ApiClient admin, string role = "TECH_STAFF", bool coordinator = false)
    {
        var tag = Tag();
        var email = $"u{tag}@tests.local".ToLowerInvariant();
        var body = await admin.PostRawAsync("/api/users",
            $$"""{"email":"{{email}}","password":"{{Password}}","employeeNo":"E-{{tag}}","firstName":"Test","lastName":"{{tag}}","role":"{{role}}","isEventCoordinator":{{(coordinator ? "true" : "false")}}}""").Ok(201);
        return (body.Id(), email, Password);
    }

    public static async Task<Guid> Room(ApiClient admin, bool storage = false)
    {
        var tag = Tag();
        var body = await admin.PostRawAsync("/api/rooms",
            $$"""{"code":"R-{{tag}}","name":"Room {{tag}}","building":"Main","floor":1,"isStorage":{{(storage ? "true" : "false")}}}""").Ok(201);
        return body.Id();
    }

    public static async Task<(Guid Id, string Code)> Station(ApiClient admin, Guid roomId, string type = "COUNTER_KIOSK")
    {
        var code = "K-" + Tag();
        var body = await admin.PostRawAsync("/api/stations",
            $$"""{"stationCode":"{{code}}","type":"{{type}}","roomId":"{{roomId}}","name":"Station {{code}}"}""").Ok(201);
        return (body.Id(), code);
    }

    public static async Task<Guid> Category(ApiClient admin, bool borrowable = true)
    {
        var body = await admin.PostRawAsync("/api/asset-categories",
            $$"""{"name":"Cat-{{Tag()}}","description":"test","isBorrowable":{{(borrowable ? "true" : "false")}}}""").Ok(201);
        return body.Id();
    }

    public static async Task<(Guid Id, string Code, string RfidTag)> BorrowableAsset(ApiClient admin, Guid categoryId, Guid homeRoomId)
    {
        var tag = Tag();
        var rfid = Hex(10);
        var body = await admin.PostRawAsync("/api/assets",
            $$"""{"assetCode":"LT-{{tag}}","name":"Laptop {{tag}}","categoryId":"{{categoryId}}","type":"BORROWABLE","brand":"Dell","model":"Latitude","serialNo":"SN-{{tag}}","rfidTagUid":"{{rfid}}","homeRoomId":"{{homeRoomId}}","condition":"GOOD","replacementCost":45000}""").Ok(201);
        return (body.Id(), $"LT-{tag}", rfid);
    }

    public static async Task<(Guid Id, string Code)> Section(ApiClient admin, string program = "BSCS", short yearLevel = 3)
    {
        var code = $"{program}-{Tag()}";
        var body = await admin.PostRawAsync("/api/sections",
            $$"""{"code":"{{code}}","program":"{{program}}","yearLevel":{{yearLevel}},"schoolYear":"{{SchoolYear}}"}""").Ok(201);
        return (body.Id(), code);
    }

    public static string RosterCsv(string sectionCode, IEnumerable<string> studentNos, string program = "BSCS", int yearLevel = 3) =>
        "student_no,first_name,last_name,section_code,program,year_level,school_year\n" +
        string.Concat(studentNos.Select(n => $"{n},First{n},Last{n},{sectionCode},{program},{yearLevel},{SchoolYear}\n"));

    public static Task<HttpResponseMessage> ImportRoster(ApiClient client, string csv, string query = "") =>
        client.PostFileAsync("/api/students:import" + query, "roster.csv", Encoding.UTF8.GetBytes(csv), "text/csv");

    /// <summary>Imports one student into a fresh section and returns the student id.</summary>
    public static async Task<(Guid Id, string StudentNo, Guid SectionId, string SectionCode)> Student(ApiClient admin)
    {
        var (sectionId, code) = await Section(admin);
        var no = "S" + Tag();
        await ImportRoster(admin, RosterCsv(code, [no])).Ok();
        var list = await admin.GetAsync($"/api/students?q={no}").Ok();
        return (list.GetProperty("items")[0].Id(), no, sectionId, code);
    }

    // Smallest valid PNG (1x1 pixel).
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
