namespace Tams.Api.Tests.Integration;

/// <summary>TAMS-119: damaged items are refused at the kiosk in both flows, and the kiosk always gets the item's name and condition.</summary>
[Collection("api")]
public class KioskConditionTests(ApiFixture api)
{
    [Fact]
    public async Task Checkout_refuses_a_damaged_item_with_its_own_code_and_lcd_text()
    {
        var rig = await KioskRig.Create(api);
        var damaged = await rig.Asset("DAMAGED");
        var start = await rig.Start();
        var id = KioskRig.SessionId(start);
        await rig.Fingerprint(id);

        var denied = await rig.Tap(id, damaged.RfidTag);

        Assert.Equal("FAILED", KioskRig.Status(denied));
        Assert.Equal("ASSET_DAMAGED", denied.GetProperty("failureCode").GetString());
        Assert.Equal(["Item damaged", "See lab staff", "", ""], KioskRig.Lcd(denied));
        Assert.Equal("DAMAGED", denied.GetProperty("item").GetProperty("condition").GetString());
        Assert.Equal(damaged.Code, denied.GetProperty("item").GetProperty("assetCode").GetString());
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(damaged.Id)); // never held at the counter
    }

    [Fact]
    public async Task Checkout_of_a_good_item_shows_name_and_condition()
    {
        var rig = await KioskRig.Create(api);
        var good = await rig.Asset("FAIR");
        var id = KioskRig.SessionId(await rig.Start());
        await rig.Fingerprint(id);

        var view = await rig.Tap(id, good.RfidTag);

        Assert.Equal("PHOTO", view.GetProperty("nextStep").GetString());
        var item = view.GetProperty("item");
        Assert.Equal(good.Code, item.GetProperty("assetCode").GetString());
        Assert.Equal("FAIR", item.GetProperty("condition").GetString());
        Assert.EndsWith(" - FAIR", KioskRig.Lcd(view)[1]);
        Assert.All(KioskRig.Lcd(view), l => Assert.True(l.Length <= 20));
        // The polled view and the idle poll carry the same item.
        Assert.Equal("FAIR", (await rig.Poll(id)).GetProperty("item").GetProperty("condition").GetString());
    }

    [Fact]
    public async Task Event_batch_refuses_a_damaged_item_and_carries_on()
    {
        var rig = await KioskRig.Create(api);
        await rig.RunningEvent();
        var damaged = await rig.Asset("DAMAGED");
        var good = await rig.Asset();
        var start = await rig.Start();
        Assert.Equal("EVENT_BATCH", start.GetProperty("type").GetString());
        var id = KioskRig.SessionId(start);

        var refused = await rig.Tap(id, damaged.RfidTag);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(refused));
        Assert.Equal(["Item damaged", "See lab staff", "0 borrowed", "OK when done"], KioskRig.Lcd(refused));
        Assert.Equal("DAMAGED", refused.GetProperty("item").GetProperty("condition").GetString());
        Assert.Equal("IN_STORAGE", await rig.AssetStatus(damaged.Id));

        var lent = await rig.Tap(id, good.RfidTag);
        Assert.Equal("AWAITING_ASSET", KioskRig.Status(lent));
        Assert.Equal($"Borrowed {good.Code}", KioskRig.Lcd(lent)[0]);
        Assert.EndsWith(" - GOOD", KioskRig.Lcd(lent)[1]);
        Assert.Equal("1 borrowed", KioskRig.Lcd(lent)[2]);
        Assert.NotEqual("IN_STORAGE", await rig.AssetStatus(good.Id));
    }
}
