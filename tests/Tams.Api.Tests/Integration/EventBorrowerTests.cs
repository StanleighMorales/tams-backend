using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Settings;

namespace Tams.Api.Tests.Integration;

/// <summary>E1 (TAMS-122): event loan borrower record, flag with override, coordinator freeze, liability.</summary>
[Collection("api")]
public class EventBorrowerTests(ApiFixture api)
{
    // Outside borrowers are matched by name when the ID is new, so every test gets names nobody else uses.
    readonly string tag = Seed.Tag();
    string Maria => "Maria" + tag;
    string Pedro => "Pedro" + tag;
    Task<EventScenario> NewEvent(int loans = 1) => EventScenario.Create(api, loans);


    static string Body(string name, string idNo, string? extra = null) =>
        $$"""{"name":"{{name}}","idNo":"{{idNo}}"{{(extra is null ? "" : "," + extra)}}}""";

    static string Flags(JsonElement check) => string.Join(",", check.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("code").GetString()));

    async Task<T> Db<T>(Func<TamsDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<TamsDbContext>());
    }

    // ------------------------------------------------------------ matching

    [Fact]
    public async Task A_student_whose_name_and_id_match_is_linked_to_the_loan()
    {
        var staff = await api.As("TECH_STAFF");
        var s = await Seed.Student(await api.As("ADMIN"));
        var e = await NewEvent();

        var res = await staff.PutRawAsync($"/api/loans/{e.LoanIds[0]}/borrower", Body($"last{s.StudentNo}, FIRST{s.StudentNo}", s.StudentNo)).Ok();

        Assert.Equal("STUDENT", res.GetProperty("check").GetProperty("match").GetString());
        var borrower = res.GetProperty("loan").GetProperty("borrower");
        Assert.Equal(s.Id, borrower.GetProperty("studentId").GetGuid());
        Assert.Equal(s.StudentNo, borrower.GetProperty("idNo").GetString());
        Assert.Equal(s.Id, (await Db(d => d.Loans.AsNoTracking().FirstAsync(l => l.Id == e.LoanIds[0]))).BorrowerStudentId);
    }

    [Fact]
    public async Task A_matching_id_with_a_different_name_is_a_possible_match_until_staff_confirm()
    {
        var staff = await api.As("TECH_STAFF");
        var s = await Seed.Student(await api.As("ADMIN"));
        var e = await NewEvent();
        var url = $"/api/loans/{e.LoanIds[0]}/borrower";

        var refused = await staff.PutRawAsync(url, Body("Someone Else", s.StudentNo)).Ok(409, "BORROWER_POSSIBLE_MATCH");
        Assert.Equal("POSSIBLE_MATCH", refused.GetProperty("check").GetProperty("match").GetString());
        Assert.Null((await Db(d => d.Loans.AsNoTracking().FirstAsync(l => l.Id == e.LoanIds[0]))).BorrowerName);

        var done = await staff.PutRawAsync(url, Body("Someone Else", s.StudentNo, "\"confirmPossibleMatch\":true")).Ok();
        Assert.Equal(s.Id, done.GetProperty("loan").GetProperty("borrower").GetProperty("studentId").GetGuid());
    }

    [Fact]
    public async Task A_staff_account_id_with_a_different_name_asks_for_confirmation_and_stays_unlinked()
    {
        var admin = await api.As("ADMIN");
        var (userId, _, _) = await Seed.User(admin);
        var employeeNo = (await admin.GetAsync($"/api/users/{userId}").Ok()).GetProperty("employeeNo").GetString()!;
        var e = await NewEvent();
        var url = $"/api/loans/{e.LoanIds[0]}/borrower";

        await admin.PutRawAsync(url, Body("Not The Employee", employeeNo)).Expect(409, "BORROWER_POSSIBLE_MATCH");
        var done = await admin.PutRawAsync(url, Body("Not The Employee", employeeNo, "\"confirmPossibleMatch\":true")).Ok();
        Assert.Equal(JsonValueKind.Null, done.GetProperty("loan").GetProperty("borrower").GetProperty("studentId").ValueKind);
    }

    [Fact]
    public async Task An_unknown_person_is_an_outside_borrower_and_the_whole_event_can_be_set_at_once()
    {
        var staff = await api.As("TECH_STAFF");
        var e = await NewEvent(loans: 2);
        var idNo = "EXT-" + Seed.Tag();

        var res = await staff.PutRawAsync($"/api/events/{e.EventId}/borrower", Body(Maria, idNo)).Ok();

        Assert.Equal("NONE", res.GetProperty("check").GetProperty("match").GetString());
        Assert.Equal(2, res.GetProperty("loans").GetArrayLength());
        var rows = await Db(d => d.Loans.AsNoTracking().Where(l => l.EventId == e.EventId).ToListAsync());
        Assert.All(rows, l => { Assert.Equal(Maria, l.BorrowerName); Assert.Equal(idNo, l.BorrowerIdNo); Assert.Null(l.BorrowerStudentId); });
    }

    [Fact]
    public async Task Only_event_loans_and_open_loans_take_a_borrower_and_input_is_validated()
    {
        var staff = await api.As("TECH_STAFF");
        var e = await NewEvent();
        var url = $"/api/loans/{e.LoanIds[0]}/borrower";

        await staff.PutRawAsync(url, Body(" ", "X1")).Expect(400, "VALIDATION_FAILED");
        await staff.PutRawAsync(url, """{"name":"A B"}""").Expect(400, "VALIDATION_FAILED");
        await staff.PutRawAsync($"/api/loans/{Guid.NewGuid()}/borrower", Body("A B", "X1")).Expect(404);
        await staff.PutRawAsync($"/api/events/{Guid.NewGuid()}/borrower", Body("A B", "X1")).Expect(404, "EVENT_NOT_FOUND");

        await Db(async d => { await d.Loans.Where(l => l.Id == e.LoanIds[0]).ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LoanStatus.Returned)); return 0; });
        await staff.PutRawAsync(url, Body("A B", "X1")).Expect(409, "LOAN_NOT_OPEN");
        await staff.PutRawAsync($"/api/events/{e.EventId}/borrower", Body("A B", "X1")).Expect(409, "NO_OPEN_EVENT_LOANS");
    }

    [Fact]
    public async Task Instructors_and_custodians_cannot_set_a_borrower()
    {
        var e = await NewEvent();
        foreach (var role in new[] { "INSTRUCTOR", "CUSTODIAN" })
        {
            var c = await api.As(role);
            await c.PutRawAsync($"/api/loans/{e.LoanIds[0]}/borrower", Body("A B", "X1")).Expect(403);
            await c.GetAsync("/api/borrowers/check?name=A%20B&idNo=X1").Expect(403);
        }
    }

    // ------------------------------------------------------------ flag and override

    [Fact]
    public async Task A_blocked_student_is_refused_with_details_and_technical_staff_are_notified()
    {
        var staff = await api.As("TECH_STAFF");
        var s = await Seed.Student(await api.As("ADMIN"));
        await Db(async d => { await d.Students.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Standing, StudentStanding.Blocked).SetProperty(x => x.HasActiveHold, true)); return 0; });
        var e = await NewEvent();

        var refused = await staff.PutRawAsync($"/api/loans/{e.LoanIds[0]}/borrower", Body($"First{s.StudentNo} Last{s.StudentNo}", s.StudentNo)).Ok(409, "BORROWER_FLAGGED");

        Assert.Equal("BLOCKED,ON_HOLD", Flags(refused.GetProperty("check")));
        Assert.Null((await Db(d => d.Loans.AsNoTracking().FirstAsync(l => l.Id == e.LoanIds[0]))).BorrowerName);
        var note = await Db(d => d.Notifications.AsNoTracking().SingleAsync(n => n.LoanId == e.LoanIds[0] && n.RecipientUserId == api.UserIds["TECH_STAFF"]));
        Assert.Equal("BORROWER_FLAGGED", note.Type);
        Assert.Contains(s.StudentNo, note.Body);
    }

    [Fact]
    public async Task An_override_needs_a_written_reason_and_is_saved_in_the_audit_log()
    {
        var staff = await api.As("TECH_STAFF");
        var s = await Seed.Student(await api.As("ADMIN"));
        await Db(async d => { await d.Students.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.HasActiveHold, true)); return 0; });
        var e = await NewEvent();
        var url = $"/api/loans/{e.LoanIds[0]}/borrower";
        var name = $"First{s.StudentNo} Last{s.StudentNo}";

        await staff.PutRawAsync(url, Body(name, s.StudentNo, "\"overrideReason\":\"   \"")).Expect(409, "BORROWER_FLAGGED");
        Assert.Null((await Db(d => d.Loans.AsNoTracking().FirstAsync(l => l.Id == e.LoanIds[0]))).BorrowerName);

        var done = await staff.PutRawAsync(url, Body(name, s.StudentNo, "\"overrideReason\":\"Dean signed a waiver\"")).Ok();
        Assert.True(done.GetProperty("overridden").GetBoolean());
        Assert.Equal(s.Id, done.GetProperty("loan").GetProperty("borrower").GetProperty("studentId").GetGuid());

        var audit = await Db(d => d.AuditLog.AsNoTracking().SingleAsync(a => a.EntityId == e.LoanIds[0] && a.Action == "BORROWER_FLAG_OVERRIDDEN"));
        Assert.Equal(api.UserIds["TECH_STAFF"], audit.ActorUserId);
        Assert.Contains("Dean signed a waiver", audit.After);
        Assert.Contains("ON_HOLD", audit.After);
    }

    [Fact]
    public async Task A_clean_borrower_needs_no_override_and_check_warns_without_saving()
    {
        var staff = await api.As("TECH_STAFF");
        var s = await Seed.Student(await api.As("ADMIN"));
        var name = Uri.EscapeDataString($"First{s.StudentNo} Last{s.StudentNo}");

        var check = await staff.GetAsync($"/api/borrowers/check?name={name}&idNo={s.StudentNo}").Ok();
        Assert.Equal("STUDENT", check.GetProperty("match").GetString());
        Assert.Equal("", Flags(check));

        await Db(async d => { await d.Students.Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Standing, StudentStanding.Blocked)); return 0; });
        Assert.Equal("BLOCKED", Flags(await staff.GetAsync($"/api/borrowers/check?name={name}&idNo={s.StudentNo}").Ok()));
        await staff.GetAsync("/api/borrowers/check?idNo=X1").Expect(400, "VALIDATION_FAILED");
    }

    // ------------------------------------------------------------ missing: who owes

    [Fact]
    public async Task Missing_event_loan_charges_the_linked_student_the_typed_borrower_or_the_coordinator()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);
        var e = await NewEvent(loans: 3);
        await admin.PutRawAsync($"/api/loans/{e.LoanIds[0]}/borrower", Body($"First{s.StudentNo} Last{s.StudentNo}", s.StudentNo)).Ok();
        await admin.PutRawAsync($"/api/loans/{e.LoanIds[1]}/borrower", Body(Maria, "EXT-" + Seed.Tag())).Ok();

        foreach (var id in e.LoanIds) await admin.PostRawAsync($"/api/loans/{id}/mark-missing", "{}").Expect(200);

        var rows = await Db(d => d.Liabilities.AsNoTracking().Where(l => e.LoanIds.Contains(l.LoanId)).ToListAsync());
        Assert.Equal(3, rows.Count);
        var student = rows.Single(l => l.LoanId == e.LoanIds[0]);
        Assert.Equal(s.Id, student.StudentId);
        Assert.Null(student.LiableName);
        var typed = rows.Single(l => l.LoanId == e.LoanIds[1]);
        Assert.Null(typed.StudentId);
        Assert.Equal(Maria, typed.LiableName);
        var coordinator = rows.Single(l => l.LoanId == e.LoanIds[2]);
        Assert.Equal(e.CoordinatorId, coordinator.LiableUserId);
        Assert.Null(coordinator.StudentId);
        Assert.Null(coordinator.LiableName);

        // The student ladder applies to the linked student only; the coordinator is frozen either way.
        var after = await Db(d => d.Students.AsNoTracking().FirstAsync(x => x.Id == s.Id));
        Assert.True(after.HasActiveHold);
        Assert.True(await Db(d => d.ApprovalFreezes.AnyAsync(f => f.InstructorUserId == e.CoordinatorId && f.Status == PenaltyStatus.Active)));
        var summary = await admin.GetAsync("/api/violations").Ok();
        var others = summary.GetProperty("otherLiabilities").EnumerateArray().ToList();
        Assert.Contains(others, x => x.GetProperty("id").GetGuid() == typed.Id && x.GetProperty("liableName").GetString() == Maria);
        Assert.Contains(others, x => x.GetProperty("id").GetGuid() == coordinator.Id && x.GetProperty("liableUserId").GetGuid() == e.CoordinatorId);
    }

    [Fact]
    public async Task An_outside_borrower_with_an_unsettled_event_liability_is_flagged_next_time_by_id_then_by_name()
    {
        var admin = await api.As("ADMIN");
        var first = await NewEvent();
        var idNo = "EXT-" + Seed.Tag();
        await admin.PutRawAsync($"/api/loans/{first.LoanIds[0]}/borrower", Body(Pedro, idNo)).Ok();
        await admin.PostRawAsync($"/api/loans/{first.LoanIds[0]}/mark-missing", "{}").Expect(200);

        var second = await NewEvent();
        var byId = await admin.PutRawAsync($"/api/loans/{second.LoanIds[0]}/borrower", Body(Pedro, idNo)).Ok(409, "BORROWER_FLAGGED");
        Assert.Equal("MISSING_ITEM,UNSETTLED_LIABILITY", Flags(byId.GetProperty("check")));

        // A known ID is matched by ID only; a new ID falls back to the name.
        var otherId = await admin.PutRawAsync($"/api/loans/{second.LoanIds[0]}/borrower", Body(Pedro, "EXT-" + Seed.Tag())).Ok(409, "BORROWER_FLAGGED");
        Assert.Equal("MISSING_ITEM,UNSETTLED_LIABILITY", Flags(otherId.GetProperty("check")));
        await admin.PutRawAsync($"/api/loans/{second.LoanIds[0]}/borrower", Body("Another Name", idNo)).Ok(409, "BORROWER_FLAGGED");

        // Settling clears the liability flag.
        var liability = await Db(d => d.Liabilities.AsNoTracking().FirstAsync(l => l.LoanId == first.LoanIds[0]));
        await admin.PostRawAsync($"/api/liabilities/{liability.Id}/settle", """{"status":"WAIVED"}""").Ok();
        await Db(async d => { await d.Loans.Where(l => l.Id == first.LoanIds[0]).ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LoanStatus.Returned)); return 0; });
        await admin.PutRawAsync($"/api/loans/{second.LoanIds[0]}/borrower", Body(Pedro, idNo)).Ok();
    }

    // ------------------------------------------------------------ overdue: coordinator freeze

    [Fact]
    public async Task An_overdue_event_loan_freezes_the_coordinator_and_the_kiosk_refuses_the_next_batch()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);
        var e = await NewEvent(loans: 2);
        await admin.PutRawAsync($"/api/loans/{e.LoanIds[0]}/borrower", Body($"First{s.StudentNo} Last{s.StudentNo}", s.StudentNo)).Ok();
        await admin.PutRawAsync($"/api/loans/{e.LoanIds[1]}/borrower", Body(Maria, "EXT-" + Seed.Tag())).Ok();
        var kiosk = api.NoAuth();
        string Start(string type) => $$"""{"cardUid":"{{e.CardUid}}","type":"{{type}}"}""";
        var before = await kiosk.SendAsync(HttpMethod.Post, $"/api/kiosk/{e.StationCode}/sessions", Start("EVENT_BATCH"), ("X-Station-Code", e.StationCode)).Ok();
        Assert.Equal("AWAITING_ASSET", before.GetProperty("status").GetString());

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettings>();
            var past = DateTimeOffset.UtcNow.AddMinutes(1);
            await db.Loans.Where(l => e.LoanIds.Contains(l.Id)).ExecuteUpdateAsync(u => u.SetProperty(l => l.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            foreach (var id in e.LoanIds) Assert.True(await LoanRules.OverdueAsync(db, settings, new Outbox(), id, past, default));
        }

        var freezes = await Db(d => d.ApprovalFreezes.AsNoTracking().Where(f => f.InstructorUserId == e.CoordinatorId).ToListAsync());
        Assert.Equal(2, freezes.Count);
        Assert.All(freezes, f => Assert.Equal(PenaltyStatus.Active, f.Status));
        var student = await Db(d => d.Students.AsNoTracking().FirstAsync(x => x.Id == s.Id));
        Assert.True(student.HasActiveHold);
        Assert.Equal(1, student.ActiveMarkCount);
        Assert.Equal(1, await Db(d => d.NonComplianceMarks.CountAsync(m => e.LoanIds.Contains(m.LoanId))));
        Assert.Equal(1, await Db(d => d.ProfileHolds.CountAsync(h => e.LoanIds.Contains(h.LoanId))));

        var denied = await kiosk.SendAsync(HttpMethod.Post, $"/api/kiosk/{e.StationCode}/sessions", Start("EVENT_BATCH"), ("X-Station-Code", e.StationCode)).Ok();
        Assert.Equal("FAILED", denied.GetProperty("status").GetString());
        Assert.Equal("INSTRUCTOR_FROZEN", denied.GetProperty("failureCode").GetString());

        // Returning what is out stays possible.
        var back = await kiosk.SendAsync(HttpMethod.Post, $"/api/kiosk/{e.StationCode}/sessions", Start("EVENT_RETURN"), ("X-Station-Code", e.StationCode)).Ok();
        Assert.Equal("AWAITING_ASSET", back.GetProperty("status").GetString());

        // Staff lift the freeze with a reason and the coordinator can start again.
        foreach (var f in freezes) await admin.PostRawAsync($"/api/freezes/{f.Id}/lift", """{"reason":"Items back"}""").Ok();
        var again = await kiosk.SendAsync(HttpMethod.Post, $"/api/kiosk/{e.StationCode}/sessions", Start("EVENT_BATCH"), ("X-Station-Code", e.StationCode)).Ok();
        Assert.Equal("AWAITING_ASSET", again.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Whoever_presses_Done_on_an_event_batch_is_recorded_in_the_audit_log_and_not_made_liable()
    {
        var e = await NewEvent(loans: 0);
        var kiosk = api.NoAuth();
        var start = await kiosk.SendAsync(HttpMethod.Post, $"/api/kiosk/{e.StationCode}/sessions",
            $$"""{"cardUid":"{{e.CardUid}}","type":"EVENT_BATCH"}""", ("X-Station-Code", e.StationCode)).Ok();
        var sessionId = start.GetProperty("sessionId").GetGuid();

        await kiosk.SendAsync(HttpMethod.Post, $"/api/kiosk/sessions/{sessionId}/confirm", null, ("X-Station-Code", e.StationCode)).Ok();

        var audit = await Db(d => d.AuditLog.AsNoTracking().SingleAsync(a => a.EntityId == sessionId && a.Action == "EVENT_BATCH_CLOSED"));
        Assert.Equal(e.CoordinatorId, audit.ActorUserId);
        Assert.Equal(e.StationId, audit.ActorStationId);
        Assert.False(await Db(d => d.Liabilities.AnyAsync(l => l.LiableUserId == e.CoordinatorId)));
    }
}
