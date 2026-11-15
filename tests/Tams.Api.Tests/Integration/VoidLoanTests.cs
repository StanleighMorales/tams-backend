using System.Text.Json;

namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-120: the kiosk Cancel button undoes the last tapped item in batch mode, and staff can void an unopened event loan.</summary>
[Collection("api")]
public class VoidLoanTests(ApiFixture api)
{
    async Task<(KioskRig Rig, Guid Session)> Batch()
    {
        var rig = await KioskRig.Create(api);
        await rig.RunningEvent();
        return (rig, KioskRig.SessionId(await rig.Start()));
    }

    async Task<JsonElement> History(Guid assetId) =>
        (await (await api.As("ADMIN")).GetAsync($"/api/assets/{assetId}/history").Ok()).GetProperty("items");

    [Fact]
    public async Task Cancel_in_batch_mode_undoes_the_last_item_and_keeps_the_rest()
    {
        var (rig, id) = await Batch();
        var first = await rig.Asset();
        var second = await rig.Asset();
        await rig.Tap(id, first.RfidTag);
        await rig.Tap(id, second.RfidTag);

        var undone = await rig.Cancel(id);

        Assert.Equal("AWAITING_ASSET", KioskRig.Status(undone)); // still in batch mode
        Assert.Equal($"Removed {second.Code}", KioskRig.Lcd(undone)[0]);
        Assert.Equal("1 borrowed", KioskRig.Lcd(undone)[2]);
        Assert.Equal("CANCELLED", (await rig.LoanOf(second.Id)).GetProperty("status").GetString());
        Assert.Equal("ACTIVE", (await rig.LoanOf(first.Id)).GetProperty("status").GetString());
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(second.Id));
        Assert.NotEqual("IN_STORAGE", await rig.AssetStatus(first.Id));
    }

    [Fact]
    public async Task Void_adds_ledger_and_audit_rows_and_deletes_nothing()
    {
        var (rig, id) = await Batch();
        var item = await rig.Asset();
        await rig.Tap(id, item.RfidTag);
        var loanId = (await rig.LoanOf(item.Id)).GetProperty("id").GetGuid();

        await rig.Cancel(id);

        var events = (await History(item.Id)).EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "LEDGER")
            .Select(i => i.GetProperty("event").GetString()).ToList();
        Assert.Contains("CHECKOUT", events); // the original row is still there
        Assert.Contains("VOIDED", events);
        var audit = await rig.Admin.GetAsync($"/api/audit-log?entityType=loan&entityId={loanId}").Ok();
        var row = audit.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("action").GetString() == "LOAN_VOIDED");
        Assert.Equal(rig.InstructorId, row.GetProperty("actorUserId").GetGuid()); // the coordinator at the kiosk
    }

    [Fact]
    public async Task Cancel_undoes_one_item_at_a_time_and_leaves_batch_mode_when_none_are_left()
    {
        var (rig, id) = await Batch();
        var a = await rig.Asset();
        var b = await rig.Asset();
        await rig.Tap(id, a.RfidTag);
        await rig.Tap(id, b.RfidTag);

        Assert.Equal("AWAITING_ASSET", KioskRig.Status(await rig.Cancel(id)));
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(await rig.Cancel(id)));
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(a.Id));
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(b.Id));

        var left = await rig.Cancel(id);
        Assert.Equal("CANCELLED", KioskRig.Status(left));
        Assert.True(left.GetProperty("finished").GetBoolean());
    }

    [Fact]
    public async Task Cancel_after_a_refused_tap_still_undoes_the_last_lent_item()
    {
        var (rig, id) = await Batch();
        var good = await rig.Asset();
        var damaged = await rig.Asset("DAMAGED");
        await rig.Tap(id, good.RfidTag);
        await rig.Tap(id, damaged.RfidTag); // refused, nothing lent

        await rig.Cancel(id);

        Assert.Equal("IN_STORAGE", await rig.AssetStatus(good.Id));
    }

    [Fact]
    public async Task Staff_void_an_unopened_event_loan_with_a_reason()
    {
        var (rig, id) = await Batch();
        var item = await rig.Asset();
        await rig.Tap(id, item.RfidTag);
        var loanId = (await rig.LoanOf(item.Id)).GetProperty("id").GetGuid();
        var tech = await api.As("TECH_STAFF");

        await tech.PostRawAsync($"/api/loans/{loanId}/void", "{}").Expect(400, "VALIDATION_FAILED");
        var voided = await tech.PostRawAsync($"/api/loans/{loanId}/void", """{"reason":"Wrong laptop handed over"}""").Ok();

        Assert.Equal("CANCELLED", voided.GetProperty("loan").GetProperty("status").GetString());
        Assert.Contains(voided.GetProperty("ledger").EnumerateArray(), l => l.GetProperty("event").GetString() == "VOIDED");
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(item.Id));
        var audit = await rig.Admin.GetAsync($"/api/audit-log?entityType=loan&entityId={loanId}").Ok();
        var row = audit.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("action").GetString() == "LOAN_VOIDED");
        Assert.Equal(api.UserIds["TECH_STAFF"], row.GetProperty("actorUserId").GetGuid());
        Assert.Contains("Wrong laptop handed over", row.GetProperty("after").GetRawText());

        // Voided means closed: it cannot be voided again, and the batch's Done counts what is still lent.
        await tech.PostRawAsync($"/api/loans/{loanId}/void", """{"reason":"again"}""").Expect(409, "LOAN_NOT_VOIDABLE");
        Assert.Equal("0 items borrowed", KioskRig.Lcd(await rig.Confirm(id))[1]);
    }

    [Fact]
    public async Task Void_is_for_staff_and_event_loans_only()
    {
        var (rig, id) = await Batch();
        var item = await rig.Asset();
        await rig.Tap(id, item.RfidTag);
        var loanId = (await rig.LoanOf(item.Id)).GetProperty("id").GetGuid();

        await (await api.As("CUSTODIAN")).PostRawAsync($"/api/loans/{loanId}/void", """{"reason":"x"}""").Expect(403);
        await (await api.As("TECH_STAFF")).PostRawAsync($"/api/loans/{Guid.NewGuid()}/void", """{"reason":"x"}""").Expect(404, "LOAN_NOT_FOUND");
        Assert.Equal("ACTIVE", (await rig.LoanOf(item.Id)).GetProperty("status").GetString());
    }
}
