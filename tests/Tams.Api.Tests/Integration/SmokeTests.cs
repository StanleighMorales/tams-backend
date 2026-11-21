namespace Tams.Api.Tests.Integration;

[Collection("api")]
public class SmokeTests(ApiFixture api)
{
    [Fact]
    public async Task Health_is_ok_with_database_up()
    {
        var body = await api.NoAuth().GetAsync("/api/health").Ok();
        Assert.Equal("up", body.GetProperty("database").GetString());
    }

    [Fact]
    public async Task Admin_can_list_users()
    {
        var admin = await api.As("ADMIN");
        var body = await admin.GetAsync("/api/users").Ok();
        Assert.True(body.GetProperty("total").GetInt32() >= 4, body.ToString());
    }
}
