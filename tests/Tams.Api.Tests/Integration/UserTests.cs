namespace Tams.Api.Tests.Integration;

/// <summary>TEST-DATA.md section 2.</summary>
[Collection("api")]
public class UserTests(ApiFixture api)
{
    static string NewUserJson(string tag, string role = "TECH_STAFF", string password = Seed.Password, string extra = "") =>
        $$"""{"email":"{{tag}}@tests.local","password":"{{password}}","employeeNo":"E-{{tag}}","firstName":"Tess","lastName":"Technician","role":"{{role}}"{{extra}}}""";

    [Fact]
    public async Task Create_returns_201_without_leaking_the_password()
    {
        var admin = await api.As("ADMIN");
        var tag = "c" + Seed.Tag().ToLowerInvariant();
        var res = await admin.PostRawAsync("/api/users", NewUserJson(tag, "CUSTODIAN")).Expect(201);
        var body = await res.Json();
        Assert.Equal("CUSTODIAN", body.GetProperty("role").GetString());
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.DoesNotContain("password", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_email_a_weak_password_and_a_coordinator_custodian()
    {
        var admin = await api.As("ADMIN");
        var tag = "d" + Seed.Tag().ToLowerInvariant();
        await admin.PostRawAsync("/api/users", NewUserJson(tag)).Expect(201);

        await admin.PostRawAsync("/api/users", NewUserJson(tag).Replace($"E-{tag}", "E-other")).Expect(409);
        await admin.PostRawAsync("/api/users", NewUserJson("e" + Seed.Tag().ToLowerInvariant(), password: "abc")).Expect(400);
        await admin.PostRawAsync("/api/users", NewUserJson("f" + Seed.Tag().ToLowerInvariant(), "CUSTODIAN", extra: ",\"isEventCoordinator\":true")).Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Create_rejects_an_unknown_role()
    {
        var admin = await api.As("ADMIN");
        await admin.PostRawAsync("/api/users", NewUserJson("g" + Seed.Tag().ToLowerInvariant(), "KING")).Expect(400);
    }

    [Fact]
    public async Task List_filters_by_role_and_search_and_pages()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin, "INSTRUCTOR");

        var byRole = await admin.GetAsync("/api/users?role=INSTRUCTOR&pageSize=100").Ok();
        Assert.All(byRole.GetProperty("items").EnumerateArray(), x => Assert.Equal("INSTRUCTOR", x.GetProperty("role").GetString()));

        var search = await admin.GetAsync($"/api/users?q={u.Email}").Ok();
        Assert.Equal(1, search.GetProperty("total").GetInt32());

        var page = await admin.GetAsync("/api/users?page=1&pageSize=2").Ok();
        Assert.True(page.GetProperty("items").GetArrayLength() <= 2);
        Assert.Equal(2, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task List_rejects_a_page_size_over_100()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync("/api/users?pageSize=101").Expect(400, "VALIDATION_FAILED");
        await admin.GetAsync("/api/users?pageSize=0").Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Get_by_id_works_and_an_unknown_id_is_404()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var body = await admin.GetAsync($"/api/users/{u.Id}").Ok();
        Assert.Equal(u.Email, body.GetProperty("email").GetString());
        await admin.GetAsync($"/api/users/{Guid.NewGuid()}").Expect(404);
    }

    [Fact]
    public async Task Update_replaces_the_editable_fields()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var body = await admin.PutRawAsync($"/api/users/{u.Id}",
            $$"""{"email":"{{u.Email}}","employeeNo":"E-RENAMED","firstName":"Tessa","lastName":"Technician","role":"TECH_STAFF"}""").Ok();
        Assert.Equal("Tessa", body.GetProperty("firstName").GetString());
        Assert.Equal("E-RENAMED", body.GetProperty("employeeNo").GetString());
    }

    [Fact]
    public async Task Deactivate_blocks_login_and_activate_restores_it()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        var login = $$"""{"email":"{{u.Email}}","password":"{{u.Password}}"}""";

        await admin.PostEmptyAsync($"/api/users/{u.Id}/deactivate").Expect(200);
        var blocked = await api.NoAuth().PostRawAsync("/api/auth/login", login);
        Assert.True((int)blocked.StatusCode is 401 or 403, $"deactivated user logged in: {(int)blocked.StatusCode}");

        await admin.PostEmptyAsync($"/api/users/{u.Id}/activate").Expect(200);
        await api.NoAuth().PostRawAsync("/api/auth/login", login).Expect(200);
    }

    [Fact]
    public async Task Reset_password_returns_a_temporary_password_and_kills_the_old_one()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);

        var body = await admin.PostEmptyAsync($"/api/users/{u.Id}/reset-password").Ok();
        var temp = body.GetProperty("temporaryPassword").GetString()!;
        Assert.False(string.IsNullOrEmpty(temp));

        await api.NoAuth().PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"{{u.Password}}"}""").Expect(401);
        await api.NoAuth().PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"{{temp}}"}""").Expect(200);
    }

    [Fact]
    public async Task Delete_hides_the_user_and_restore_brings_them_back()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);

        await admin.DeleteAsync($"/api/users/{u.Id}").Expect(204);
        var listed = await admin.GetAsync($"/api/users?q={u.Email}").Ok();
        Assert.Equal(0, listed.GetProperty("total").GetInt32());
        var withDeleted = await admin.GetAsync($"/api/users?q={u.Email}&includeDeleted=true").Ok();
        Assert.Equal(1, withDeleted.GetProperty("total").GetInt32());

        await admin.PostEmptyAsync($"/api/users/{u.Id}/restore").Expect(200);
        var back = await admin.GetAsync($"/api/users?q={u.Email}").Ok();
        Assert.Equal(1, back.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_deleted_user_cannot_log_in()
    {
        var admin = await api.As("ADMIN");
        var u = await Seed.User(admin);
        await admin.DeleteAsync($"/api/users/{u.Id}").Expect(204);
        await api.NoAuth().PostRawAsync("/api/auth/login", $$"""{"email":"{{u.Email}}","password":"{{u.Password}}"}""").Expect(401);
    }
}
