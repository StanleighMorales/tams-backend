using System.Text.Json;

namespace Tams.Api.Tests.Integration;

/// <summary>
/// Pins the field names of the responses the mobile app reads, as written in docs/API-SAMPLES.md. The OpenAPI spec has no
/// response types, so this is what stops a renamed or dropped field going unnoticed: if one of these fails, change the
/// code on purpose, update the sample in the doc, and tell the app team.
/// </summary>
[Collection("api")]
public class ResponseShapeTests(ApiFixture api)
{
    static string[] Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    static void Shape(JsonElement e, params string[] expected) =>
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), Keys(e));

    static readonly string[] LoanFields =
    [
        "id", "loanNo", "type", "status", "custodyState", "asset", "student", "approvedBy", "coordinator", "event", "destinationRoom",
        "checkedOutAt", "transitDeadlineAt", "verifiedAt", "classLengthMinutes", "classEndsAt", "graceMinutes", "dueAt", "overdueAt",
        "missingAt", "returnedAt", "borrower", "returnedBy", "photoUrl", "lateArrivalWarningNo", "notes",
    ];

    static readonly string[] AuditFields =
    [
        "id", "roomId", "source", "status", "startedByUserId", "startedAt", "completedAt", "signedOffByUserId", "signedOffAt",
        "expectedCount", "matchedCount", "missingCount", "misplacedCount", "unknownCount", "importFileId",
        "reportPdfUrl", "reportCsvUrl", "reportXlsxUrl",
    ];

    [Fact]
    public async Task Login_and_the_instructor_loan_screens_keep_their_shape()
    {
        var rig = await KioskRig.Create(api);
        var asset = await rig.Asset();

        // A request waiting for the instructor, then the loan it becomes.
        var s = KioskRig.SessionId(await rig.Start("CHECKOUT"));
        await rig.Fingerprint(s);
        await rig.Tap(s, asset.RfidTag);
        await rig.Photo(s);
        var pending = await rig.Instructor.GetAsync("/api/approvals/pending").Ok();
        Shape(pending, "frozen", "freezes", "items");
        Shape(pending.GetProperty("items")[0], "sessionId", "student", "asset", "items", "photoUrl", "roomOptions", "defaultRoomId",
            "classLengthOptions", "defaultClassLengthMinutes", "requestedAt", "expiresAt");
        await rig.Approve(s);
        await rig.Confirm(s);

        var list = await rig.Instructor.GetAsync("/api/loans?approvedBy=me&pageSize=2").Ok();
        Shape(list, "items", "page", "pageSize", "total");
        Shape(list.GetProperty("items")[0], LoanFields);

        var detail = await rig.Instructor.GetAsync("/api/loans/" + (await rig.LoanOf(asset.Id)).Id()).Ok();
        Shape(detail, "loan", "approval", "ledger");
        Shape(detail.GetProperty("loan"), LoanFields);

        Shape(await rig.Instructor.GetAsync("/api/notifications?pageSize=2").Ok(), "items", "page", "pageSize", "total", "unreadCount");
        Shape(await rig.Instructor.PostRawAsync("/api/device-tokens", $$"""{"token":"shape-{{Seed.Tag()}}","platform":"ANDROID"}""").Ok(),
            "id", "platform", "lastSeenAt");
    }

    [Fact]
    public async Task Login_returns_tokens_and_the_user()
    {
        var login = await api.NoAuth().PostRawAsync("/api/auth/login",
            $$"""{"email":"instructor@tests.local","password":"{{ApiFixture.RolePassword}}"}""").Ok();
        Shape(login, "accessToken", "accessTokenExpiresAt", "refreshToken", "user");
        Shape(login.GetProperty("user"), "id", "email", "firstName", "lastName", "role", "isEventCoordinator");
    }

    [Fact]
    public async Task A_custodian_audit_keeps_its_shape_from_roster_to_report()
    {
        var rig = await KioskRig.Create(api);
        var asset = await rig.Asset();
        var cust = await api.As("CUSTODIAN");

        var roster = await cust.GetAsync($"/api/rooms/{rig.StorageRoomId}/roster").Ok();
        Shape(roster, "room", "items", "downloadedAt");
        Shape(roster.GetProperty("items")[0], "id", "assetCode", "name", "category", "type", "status", "serialNo", "qrPayload");

        var start = await cust.PostRawAsync("/api/audits", $$"""{"roomId":"{{rig.StorageRoomId}}"}""").Ok(201);
        Shape(start, "audit", "room", "roster");
        Shape(start.GetProperty("audit"), AuditFields);
        var id = start.GetProperty("audit").Id();

        var batch = await cust.PostRawAsync($"/api/audits/{id}/scans:batch",
            $$"""{"scans":[{"clientScanId":"{{Guid.NewGuid()}}","scannedValue":"tams:asset:{{asset.Id}}","scannedAt":"{{DateTimeOffset.UtcNow:O}}"}]}""").Ok();
        Shape(batch, "auditId", "accepted", "duplicates", "results");
        Shape(batch.GetProperty("results")[0], "clientScanId", "result", "assetId", "duplicate", "assetCode", "name");

        var done = await cust.PostRawAsync($"/api/audits/{id}/complete", "{}").Ok();
        Shape(done, "audit", "items");
        Shape(done.GetProperty("audit"), AuditFields);
        Shape(done.GetProperty("items")[0], "id", "result", "assetId", "assetCode", "name", "scannedValue", "scannedAt", "syncedAt");
        Shape(await cust.GetAsync($"/api/audits/{id}").Ok(), "audit", "items");
    }
}
