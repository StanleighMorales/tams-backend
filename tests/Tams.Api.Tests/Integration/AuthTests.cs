namespace Tams.Api.Tests.Integration;

/// <summary>TEST-DATA.md section 1. Every test that fails logins or changes passwords uses its own user.</summary>
[Collection("api")]
public class AuthTests(ApiFixture api)
{
    async Task<(string Email, string Access, string Refresh)> LoginAsync(ApiClient anon, string email, string password)
    {
        var body = await anon.PostRawAsync("/api/auth/login", $$"""{"email":"{{email}}","password":"{{password}}"}""").Ok();
        return (email, body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    [Fact]
    public async Task Health_is_public()
    {
        var body = await api.NoAuth().GetAsync("/api/health").Ok();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("up", body.GetProperty("database").GetString());
    }

    [Fact]
    public async Task Login_returns_tokens_and_the_user()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin, "CUSTODIAN");
        var body = await api.NoAuth().PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"{{u.Password}}"}""").Ok();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
        Assert.Equal("CUSTODIAN", body.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_401()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        await api.NoAuth().PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"wrong-password"}""")
            .Expect(401, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Login_for_an_unknown_email_is_401_not_404()
    {
        await api.NoAuth().PostRawAsync("/api/auth/login", """{"email":"nobody@tests.local","password":"whatever123"}""")
            .Expect(401, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Login_without_credentials_is_400()
    {
        await api.NoAuth().PostRawAsync("/api/auth/login", "{}").Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var anon = api.NoAuth();
        for (var i = 0; i < 10; i++)
        {
            var r = await anon.PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"wrong-password"}""");
            if ((int)r.StatusCode == 423) return; // locked: the lockout works
        }
        // Once locked even the right password is refused.
        await anon.PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"{{u.Password}}"}""").Expect(423, "ACCOUNT_LOCKED");
    }

    [Fact]
    public async Task Refresh_issues_a_new_pair_and_reusing_the_old_token_revokes_everything()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var anon = api.NoAuth();
        var first = await LoginAsync(anon, u.Email, u.Password);

        var second = await anon.PostRawAsync("/api/auth/refresh", $$"""{"refreshToken":"{{first.Refresh}}"}""").Ok();
        var newRefresh = second.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(first.Refresh, newRefresh);

        // Replaying the already-used token is treated as theft: 401 and the new token dies too.
        await anon.PostRawAsync("/api/auth/refresh", $$"""{"refreshToken":"{{first.Refresh}}"}""").Expect(401, "REFRESH_TOKEN_REUSED");
        await anon.PostRawAsync("/api/auth/refresh", $$"""{"refreshToken":"{{newRefresh}}"}""").Expect(401);
    }

    [Fact]
    public async Task Refresh_with_garbage_is_401_and_without_a_token_is_400()
    {
        var anon = api.NoAuth();
        await anon.PostRawAsync("/api/auth/refresh", """{"refreshToken":"not-a-real-token"}""").Expect(401, "INVALID_REFRESH_TOKEN");
        await anon.PostRawAsync("/api/auth/refresh", "{}").Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Change_password_switches_the_login_password()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var anon = api.NoAuth();
        var session = await LoginAsync(anon, u.Email, u.Password);
        var me = api.WithToken(session.Access);

        await me.PostRawAsync("/api/auth/change-password", $$"""{"currentPassword":"{{u.Password}}","newPassword":"New#Pass12345"}""").Expect(204);

        await anon.PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"{{u.Password}}"}""").Expect(401);
        await LoginAsync(anon, u.Email, "New#Pass12345");
    }

    [Fact]
    public async Task Change_password_rejects_a_wrong_current_password_a_same_password_and_a_short_one()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var me = api.WithToken((await LoginAsync(api.NoAuth(), u.Email, u.Password)).Access);

        await me.PostRawAsync("/api/auth/change-password", """{"currentPassword":"not-my-password","newPassword":"New#Pass12345"}""")
            .Expect(400, "INVALID_CURRENT_PASSWORD");
        await me.PostRawAsync("/api/auth/change-password", $$"""{"currentPassword":"{{u.Password}}","newPassword":"{{u.Password}}"}""").Expect(400);
        await me.PostRawAsync("/api/auth/change-password", $$"""{"currentPassword":"{{u.Password}}","newPassword":"abc"}""").Expect(400);
    }

    [Fact]
    public async Task Logout_revokes_the_given_refresh_token()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var anon = api.NoAuth();
        var session = await LoginAsync(anon, u.Email, u.Password);

        await api.WithToken(session.Access).PostRawAsync("/api/auth/logout", $$"""{"refreshToken":"{{session.Refresh}}"}""").Expect(204);
        await anon.PostRawAsync("/api/auth/refresh", $$"""{"refreshToken":"{{session.Refresh}}"}""").Expect(401);
    }

    [Fact]
    public async Task Logout_without_a_body_revokes_every_session()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var anon = api.NoAuth();
        var a = await LoginAsync(anon, u.Email, u.Password);
        var b = await LoginAsync(anon, u.Email, u.Password);

        await api.WithToken(a.Access).PostRawAsync("/api/auth/logout", "{}").Expect(204);
        await anon.PostRawAsync("/api/auth/refresh", $$"""{"refreshToken":"{{a.Refresh}}"}""").Expect(401);
        await anon.PostRawAsync("/api/auth/refresh", $$"""{"refreshToken":"{{b.Refresh}}"}""").Expect(401);
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/rooms")]
    [InlineData("/api/assets")]
    [InlineData("/api/students")]
    [InlineData("/api/settings")]
    [InlineData("/api/audit-log")]
    public async Task Protected_routes_reject_a_missing_or_invalid_token_with_401(string url)
    {
        await api.NoAuth().GetAsync(url).Expect(401);
        await api.WithToken("not.a.jwt").GetAsync(url).Expect(401);
    }

    [Fact]
    public async Task An_unparseable_body_is_400_not_500()
    {
        await api.NoAuth().PostRawAsync("/api/auth/login", "{not json").Expect(400);
    }
}
