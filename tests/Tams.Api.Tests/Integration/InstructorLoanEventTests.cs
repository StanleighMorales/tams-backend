using Tams.Api.Live;

namespace Tams.Api.Tests.Integration;

/// <summary>
/// The mobile app's My Loans screen listens on the instructor's own user:{id} group, so loan.created and asset.statusChanged
/// must reach that group as well as "staff" (PRD 6.4). Real checkout and room-scan flows, events read from the hub recorder.
/// </summary>
[Collection("api")]
public class InstructorLoanEventTests(ApiFixture api)
{
    [Fact]
    public async Task Checkout_tells_the_approving_instructor_and_staff_about_the_new_loan()
    {
        var rig = await KioskRig.Create(api);
        var asset = await rig.Asset();
        api.Hub.Clear();
        await rig.Checkout(asset.RfidTag);

        var id = asset.Id.ToString();
        foreach (var evt in new[] { "loan.created", "asset.statusChanged" })
        {
            var groups = api.Hub.GroupsReceiving(evt, id);
            Assert.Contains(LiveHub.StaffGroup, groups);
            Assert.Contains(LiveHub.UserGroup(rig.InstructorId), groups);
        }
    }

    [Fact]
    public async Task A_room_check_in_reaches_the_instructor_who_approved_the_loan_and_nobody_else()
    {
        var rig = await KioskRig.Create(api);
        var asset = await rig.Asset();
        await rig.Checkout(asset.RfidTag);
        var (_, reader) = await Seed.Station(rig.Admin, rig.DestinationRoomId, "ROOM_STATION");
        api.Hub.Clear();

        var scan = await api.NoAuth().PostRawAsync($"/api/stations/{reader}/scans", $$"""{"tagUid":"{{asset.RfidTag}}"}""").Ok();
        Assert.Equal("VERIFIED", scan.GetProperty("result").GetString());

        var groups = api.Hub.GroupsReceiving("asset.statusChanged", asset.Id.ToString());
        Assert.Contains(LiveHub.StaffGroup, groups);
        Assert.Contains(LiveHub.UserGroup(rig.InstructorId), groups);
        // Another instructor's group never hears about this loan.
        Assert.DoesNotContain(LiveHub.UserGroup(api.UserIds["INSTRUCTOR"]), groups);
    }
}
