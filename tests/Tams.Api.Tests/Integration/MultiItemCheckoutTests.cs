using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-121: several items per student checkout and return, set by max_items_per_transaction (default 1).</summary>
[Collection("api")]
public class MultiItemCheckoutTests(ApiFixture api)
{
    static string[] Lcd(JsonElement v) => KioskRig.Lcd(v);

    async Task<(Guid Id, JsonElement First)> StartItemStep(KioskRig rig)
    {
        var id = KioskRig.SessionId(await rig.Start("CHECKOUT"));
        return (id, await rig.Fingerprint(id));
    }

    // ------------------------------------------------------------ limit 1 (the PRD flow)

    [Fact]
    public async Task With_the_default_limit_of_one_the_first_tap_goes_straight_to_the_photo()
    {
        var rig = await KioskRig.Create(api);
        var item = await rig.Asset();
        var (id, afterFinger) = await StartItemStep(rig);
        Assert.Equal(["Tap item tag", "", ""], Lcd(afterFinger)[1..]); // no "Up to N items" at limit 1

        var tapped = await rig.Tap(id, item.RfidTag);

        Assert.Equal("PHOTO", tapped.GetProperty("nextStep").GetString());
        Assert.Equal(1, tapped.GetProperty("items").GetArrayLength());
        await rig.TapRaw(id, (await rig.Asset()).RfidTag).Expect(409, "INVALID_SESSION_STATE"); // a second item is not accepted
    }

    // ------------------------------------------------------------ limit above 1

    [Fact]
    public async Task Three_items_in_one_checkout_make_three_loans_sharing_room_due_time_and_approval()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        var a = await rig.Asset();
        var b = await rig.Asset("FAIR");
        var id = KioskRig.SessionId(await rig.Start("CHECKOUT"));
        var afterFinger = await rig.Fingerprint(id);
        Assert.Equal("Up to 3 items", Lcd(afterFinger)[2]);

        var first = await rig.Tap(id, a.RfidTag);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(first)); // waits for more
        Assert.Equal($"Added {a.Code}", Lcd(first)[0]);
        Assert.EndsWith(" - GOOD", Lcd(first)[1]);
        Assert.Equal(["1 of 3   X=undo", "Scan item or OK"], Lcd(first)[2..]);
        var second = await rig.Tap(id, b.RfidTag);
        Assert.Equal("2 of 3   X=undo", Lcd(second)[2]);
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.Equal("FAIR", second.GetProperty("items")[1].GetProperty("condition").GetString());

        var photo = await rig.Confirm(id); // OK ends the item step
        Assert.Equal("PHOTO", photo.GetProperty("nextStep").GetString());
        await rig.Photo(id);

        // The instructor sees both items; `asset` still holds the first one for the older mobile build.
        var pending = (await rig.Instructor.GetAsync("/api/approvals/pending").Ok()).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("sessionId").GetGuid() == id);
        Assert.Equal(a.Code, pending.GetProperty("asset").GetProperty("assetCode").GetString());
        Assert.Equal([a.Code, b.Code], pending.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetCode").GetString()!).ToArray());

        var approved = await rig.Approve(id);
        Assert.Equal("APPROVED", approved.GetProperty("decision").GetString());
        var confirmView = await rig.Poll(id);
        Assert.Equal("CONFIRM", confirmView.GetProperty("nextStep").GetString());
        Assert.StartsWith("2 items", Lcd(confirmView)[1]);
        Assert.All(Lcd(confirmView), l => Assert.True(l.Length <= 20, l));

        var done = await rig.Confirm(id);
        Assert.Equal("COMPLETED", KioskRig.Status(done));
        var loanA = await rig.LoanOf(a.Id);
        var loanB = await rig.LoanOf(b.Id);
        Assert.NotEqual(loanA.GetProperty("loanNo").GetString(), loanB.GetProperty("loanNo").GetString());
        Assert.Equal(loanA.GetProperty("dueAt").GetDateTimeOffset(), loanB.GetProperty("dueAt").GetDateTimeOffset());
        Assert.Equal(loanA.GetProperty("destinationRoom").GetProperty("id").GetGuid(), loanB.GetProperty("destinationRoom").GetProperty("id").GetGuid());
        Assert.Equal("IN_TRANSIT", await rig.AssetStatus(a.Id));
        Assert.Equal("IN_TRANSIT", await rig.AssetStatus(b.Id));

        // One approval and one ledger CHECKOUT row per loan.
        var detailA = await rig.Admin.GetAsync($"/api/loans/{loanA.GetProperty("id").GetGuid()}").Ok();
        var detailB = await rig.Admin.GetAsync($"/api/loans/{loanB.GetProperty("id").GetGuid()}").Ok();
        Assert.Equal(detailA.GetProperty("approval").GetProperty("id").GetGuid(), detailB.GetProperty("approval").GetProperty("id").GetGuid());
        Assert.Single(detailA.GetProperty("ledger").EnumerateArray(), l => l.GetProperty("event").GetString() == "CHECKOUT");
        Assert.Single(detailB.GetProperty("ledger").EnumerateArray(), l => l.GetProperty("event").GetString() == "CHECKOUT");
    }

    [Fact]
    public async Task Reaching_the_limit_moves_to_the_photo_by_itself()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 2, maxActive: 5);
        var (id, _) = await StartItemStep(rig);

        await rig.Tap(id, (await rig.Asset()).RfidTag);
        var last = await rig.Tap(id, (await rig.Asset()).RfidTag);

        Assert.Equal("PHOTO", last.GetProperty("nextStep").GetString());
        Assert.Equal(["2 of 2 items", "Max reached", "Next: photo"], Lcd(last)[1..]);
    }

    [Fact]
    public async Task The_active_loan_cap_can_be_the_lower_limit()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 5, maxActive: 2);
        var (id, afterFinger) = await StartItemStep(rig);
        Assert.Equal(["Up to 2 items", "(loan limit)"], Lcd(afterFinger)[2..]);

        await rig.Tap(id, (await rig.Asset()).RfidTag);
        var last = await rig.Tap(id, (await rig.Asset()).RfidTag);

        Assert.Equal("PHOTO", last.GetProperty("nextStep").GetString());
        Assert.Equal("Loan limit reached", Lcd(last)[2]); // names the limit that was hit
    }

    [Fact]
    public async Task A_student_with_an_open_loan_gets_only_what_is_left_of_the_cap()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        await rig.Checkout((await rig.Asset()).RfidTag, (await rig.Asset()).RfidTag); // two loans open

        var (id, afterFinger) = await StartItemStep(rig);

        Assert.Equal("", Lcd(afterFinger)[2]); // one left: back to the single-item flow
        Assert.Equal("PHOTO", (await rig.Tap(id, (await rig.Asset()).RfidTag)).GetProperty("nextStep").GetString());
    }

    // ------------------------------------------------------------ per-item checks and the loop

    [Fact]
    public async Task A_repeated_tag_is_refused_and_the_checkout_carries_on()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        var a = await rig.Asset();
        var (id, _) = await StartItemStep(rig);
        await rig.Tap(id, a.RfidTag);

        var again = await rig.Tap(id, a.RfidTag);

        Assert.Equal("AWAITING_ASSET", KioskRig.Status(again));
        Assert.Equal(["Already added", a.Code, "1 of 3   X=undo", "Scan item or OK"], Lcd(again));
        Assert.Equal(1, again.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_refused_item_after_the_first_keeps_the_checkout_but_the_first_refusal_denies_it()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        var good = await rig.Asset();
        var damaged = await rig.Asset("DAMAGED");

        var (id, _) = await StartItemStep(rig);
        await rig.Tap(id, good.RfidTag);
        var refused = await rig.Tap(id, damaged.RfidTag);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(refused));
        Assert.Equal(["Item damaged", "See lab staff", "1 of 3   X=undo", "Scan item or OK"], Lcd(refused));
        var unknown = await rig.Tap(id, Seed.Hex(10));
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(unknown));
        Assert.Equal("Unknown item tag", Lcd(unknown)[0]);

        // With no item yet, a refusal denies the borrow as the PRD says.
        var (id2, _) = await StartItemStep(rig);
        var denied = await rig.Tap(id2, damaged.RfidTag);
        Assert.Equal("FAILED", KioskRig.Status(denied));
        Assert.Equal("ASSET_DAMAGED", denied.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task OK_with_no_item_does_nothing_and_X_undoes_items_one_by_one_then_cancels()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        var a = await rig.Asset();
        var b = await rig.Asset();
        var (id, afterFinger) = await StartItemStep(rig);

        var ok = await rig.Confirm(id);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(ok));
        Assert.Equal(Lcd(afterFinger), Lcd(ok)); // the screen is unchanged

        await rig.Tap(id, a.RfidTag);
        await rig.Tap(id, b.RfidTag);
        var undo1 = await rig.Cancel(id);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(undo1));
        Assert.Equal($"Removed {b.Code}", Lcd(undo1)[0]);
        Assert.Equal("1 of 3   X=undo", Lcd(undo1)[2]);
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(b.Id));
        Assert.Equal("AT_COUNTER", await rig.AssetStatus(a.Id));
        Assert.Equal(1, undo1.GetProperty("items").GetArrayLength());

        var undo2 = await rig.Cancel(id);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(undo2));
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(a.Id));
        Assert.Equal("Up to 3 items", Lcd(undo2)[2]);

        var cancelled = await rig.Cancel(id); // nothing left: X cancels the checkout
        Assert.Equal("CANCELLED", KioskRig.Status(cancelled));
    }

    [Fact]
    public async Task Cancelling_after_the_item_step_releases_every_item()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        var a = await rig.Asset();
        var b = await rig.Asset();
        var (id, _) = await StartItemStep(rig);
        await rig.Tap(id, a.RfidTag);
        await rig.Tap(id, b.RfidTag);
        await rig.Confirm(id);
        await rig.Photo(id);

        await rig.Instructor.PostEmptyAsync($"/api/approvals/{id}/decline").Expect(200);

        Assert.Equal("IN_STORAGE", await rig.AssetStatus(a.Id));
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(b.Id));
    }

    // ------------------------------------------------------------ return

    [Fact]
    public async Task Return_takes_several_items_after_one_fingerprint_and_OK_means_done()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        var a = await rig.Asset();
        var b = await rig.Asset();
        await rig.Checkout(a.RfidTag, b.RfidTag);

        var id = KioskRig.SessionId(await rig.Start("RETURN"));
        await rig.Fingerprint(id);
        var first = await rig.Tap(id, a.RfidTag);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(first)); // does not end after the first item
        Assert.Equal([$"Returned {a.Code}", "1 returned", "Tap next item", "OK when done"], Lcd(first));
        var second = await rig.Tap(id, b.RfidTag);
        Assert.Equal("2 returned", Lcd(second)[1]);
        var miss = await rig.Tap(id, Seed.Hex(10)); // refused, but the session carries on
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(miss));

        var done = await rig.Confirm(id);

        Assert.Equal("COMPLETED", KioskRig.Status(done));
        Assert.Equal(["Done", "2 returned"], Lcd(done)[..2]);
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(a.Id));
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(b.Id));
    }

    // ------------------------------------------------------------ penalties per transaction

    async Task Sql(Func<TamsDbContext, Task> work)
    {
        using var scope = api.Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<TamsDbContext>());
    }

    async Task<JsonElement> WaitForStanding(KioskRig rig, Func<JsonElement, bool> ready)
    {
        for (var i = 0; i < 60; i++) // the sweeper ticks every 15 s
        {
            var s = await rig.Admin.GetAsync($"/api/students/{rig.StudentId}/standing").Ok();
            if (ready(s)) return s;
            await Task.Delay(1000);
        }
        throw new Xunit.Sdk.XunitException("The sweeper did not issue the penalty in time.");
    }

    [Fact]
    public async Task Missing_the_room_tap_for_three_items_gives_one_warning()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        await rig.Checkout((await rig.Asset()).RfidTag, (await rig.Asset()).RfidTag, (await rig.Asset()).RfidTag);

        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Sql(db => db.Loans.Where(l => l.StudentId == rig.StudentId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.TransitDeadlineAt, past)));
        var standing = await WaitForStanding(rig, s => s.GetProperty("student").GetProperty("activeWarningCount").GetInt32() > 0);

        // Each item still raises its own anomaly; wait until the sweeper has handled all three loans.
        var loanIds = (await rig.Admin.GetAsync($"/api/loans?studentId={rig.StudentId}").Ok()).GetProperty("items").EnumerateArray()
            .Select(l => l.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(3, loanIds.Count);
        for (var i = 0; i < 60; i++)
        {
            var handled = 0;
            foreach (var id in loanIds)
                handled += (await rig.Admin.GetAsync($"/api/anomalies?loanId={id}").Ok()).GetProperty("items").GetArrayLength() > 0 ? 1 : 0;
            if (handled == 3) break;
            await Task.Delay(1000);
        }

        standing = await rig.Admin.GetAsync($"/api/students/{rig.StudentId}/standing").Ok();
        Assert.Equal(1, standing.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task Three_overdue_items_give_one_mark()
    {
        var rig = await KioskRig.Create(api);
        await using var _ = await rig.Settings(maxItems: 3, maxActive: 3);
        await rig.Checkout((await rig.Asset()).RfidTag, (await rig.Asset()).RfidTag, (await rig.Asset()).RfidTag);

        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Sql(db => db.Loans.Where(l => l.StudentId == rig.StudentId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.DueAt, past).SetProperty(l => l.TransitDeadlineAt, (DateTimeOffset?)null)
                .SetProperty(l => l.CustodyState, Tams.Api.Data.CustodyState.Verified)));
        await WaitForStanding(rig, s => s.GetProperty("holds").GetArrayLength() == 3);

        var standing = await rig.Admin.GetAsync($"/api/students/{rig.StudentId}/standing").Ok();
        Assert.Equal(1, standing.GetProperty("marks").GetArrayLength());
        Assert.Equal(3, standing.GetProperty("holds").GetArrayLength());
    }

    // ------------------------------------------------------------ the setting

    [Fact]
    public async Task The_setting_defaults_to_one_and_technical_staff_may_edit_only_this_one()
    {
        var admin = await api.As("ADMIN");
        var tech = await api.As("TECH_STAFF");
        var s = await admin.GetAsync("/api/settings/max_items_per_transaction").Ok();
        Assert.Equal(1, s.GetProperty("default").GetInt32());
        Assert.True(s.GetProperty("techStaffCanEdit").GetBoolean());

        try
        {
            Assert.Equal(4, (await tech.PutRawAsync("/api/settings/max_items_per_transaction", """{"value":4}""").Ok()).GetProperty("value").GetInt32());
            await tech.PutRawAsync("/api/settings", """{"max_items_per_transaction":2}""").Expect(200);
            await tech.PutRawAsync("/api/settings/grace_minutes", """{"value":20}""").Expect(403, "SETTING_ADMIN_ONLY");
            await tech.PutRawAsync("/api/settings", """{"max_items_per_transaction":2,"grace_minutes":20}""").Expect(403, "SETTING_ADMIN_ONLY");
            await (await api.As("CUSTODIAN")).PutRawAsync("/api/settings/max_items_per_transaction", """{"value":2}""").Expect(403);
            await admin.PutRawAsync("/api/settings/max_items_per_transaction", """{"value":0}""").Expect(400);
            await admin.PutRawAsync("/api/settings/max_items_per_transaction", """{"value":11}""").Expect(400);
        }
        finally { await admin.PutRawAsync("/api/settings/max_items_per_transaction", """{"value":1}""").Expect(200); }
    }
}
