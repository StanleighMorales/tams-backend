using System.Text;

namespace Tams.Api.Tests.Integration;

/// <summary>TEST-DATA.md sections 8 (sections) and 9 (students and roster import).</summary>
[Collection("api")]
public class StudentTests(ApiFixture api)
{
    // ------------------------------------------------------------ sections

    [Fact]
    public async Task Section_create_get_update_and_list()
    {
        var admin = await api.As("ADMIN");
        var adviser = (await Seed.User(admin, "INSTRUCTOR")).Id;
        var code = "BSCS-" + Seed.Tag();

        var created = await admin.PostRawAsync("/api/sections",
            $$"""{"code":"{{code}}","program":"BSCS","yearLevel":3,"schoolYear":"2026-2027","adviserUserId":"{{adviser}}"}""").Ok(201);
        var id = created.Id();

        var got = await admin.GetAsync($"/api/sections/{id}").Ok();
        Assert.Equal(code, got.GetProperty("code").GetString());

        var updated = await admin.PutRawAsync($"/api/sections/{id}",
            $$"""{"code":"{{code}}","program":"BSCS","yearLevel":4,"schoolYear":"2026-2027"}""").Ok();
        Assert.Equal(4, updated.GetProperty("yearLevel").GetInt32());

        var list = await admin.GetAsync($"/api/sections?schoolYear=2026-2027&program=BSCS&q={code}").Ok();
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Section_create_rejects_a_duplicate_in_the_same_year_and_a_bad_school_year()
    {
        var admin = await api.As("ADMIN");
        var code = "BSIT-" + Seed.Tag();
        var json = $$"""{"code":"{{code}}","program":"BSIT","yearLevel":1,"schoolYear":"2026-2027"}""";
        await admin.PostRawAsync("/api/sections", json).Expect(201);
        await admin.PostRawAsync("/api/sections", json).Expect(409);

        // The same code in another school year is allowed.
        await admin.PostRawAsync("/api/sections", json.Replace("2026-2027", "2027-2028")).Expect(201);

        await admin.PostRawAsync("/api/sections", json.Replace(code, "X-" + Seed.Tag()).Replace("2026-2027", "2026-2028")).Expect(400, "VALIDATION_FAILED");
        await admin.PostRawAsync("/api/sections", json.Replace(code, "Y-" + Seed.Tag()).Replace("\"yearLevel\":1", "\"yearLevel\":99")).Expect(400);
    }

    [Fact]
    public async Task Section_get_unknown_id_is_404()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync($"/api/sections/{Guid.NewGuid()}").Expect(404);
    }

    // ------------------------------------------------------------ roster import

    [Fact]
    public async Task Import_creates_students_then_is_idempotent()
    {
        var admin = await api.As("ADMIN");
        var (_, code) = await Seed.Section(admin);
        var nos = Enumerable.Range(0, 4).Select(_ => "S" + Seed.Tag()).ToArray();
        var csv = Seed.RosterCsv(code, nos);

        var first = await Seed.ImportRoster(admin, csv).Ok();
        Assert.True(first.GetProperty("committed").GetBoolean());
        Assert.Equal(4, first.GetProperty("created").GetInt32());
        Assert.Equal(0, first.GetProperty("errors").GetArrayLength());

        var second = await Seed.ImportRoster(admin, csv).Ok();
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(4, second.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public async Task Import_dry_run_reports_without_saving()
    {
        var admin = await api.As("ADMIN");
        var (_, code) = await Seed.Section(admin);
        var no = "S" + Seed.Tag();

        var report = await Seed.ImportRoster(admin, Seed.RosterCsv(code, [no]), "?dryRun=true").Ok();
        Assert.False(report.GetProperty("committed").GetBoolean());
        Assert.Equal(1, report.GetProperty("created").GetInt32());
        Assert.Equal(0, (await admin.GetAsync($"/api/students?q={no}").Ok()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Import_creates_a_missing_section_and_handles_quoted_commas()
    {
        var admin = await api.As("ADMIN");
        var code = "NEW-" + Seed.Tag();
        var no = "S" + Seed.Tag();
        var csv = $"student_no,first_name,last_name,section_code,program,year_level,school_year\n{no},Maria,\"Santos, Jr.\",{code},BSCS,3,2026-2027\n";

        var report = await Seed.ImportRoster(admin, csv).Ok();
        Assert.Equal(1, report.GetProperty("sectionsCreated").GetInt32());

        var student = (await admin.GetAsync($"/api/students?q={no}").Ok()).GetProperty("items")[0];
        Assert.Equal("Santos, Jr.", student.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task Import_lenient_keeps_good_rows_and_strict_rejects_the_whole_file()
    {
        var admin = await api.As("ADMIN");
        var (_, code) = await Seed.Section(admin);
        string Csv(string good) =>
            "student_no,first_name,last_name,section_code,program,year_level,school_year\n" +
            $",NoNumber,X,{code},BSCS,3,2026-2027\n" +
            $"S{Seed.Tag()},Bad,Year,{code},BSCS,99,2026-2027\n" +
            $"{good},Good,Row,{code},BSCS,3,2026-2027\n";

        var lenientNo = "S" + Seed.Tag();
        var lenient = await Seed.ImportRoster(admin, Csv(lenientNo)).Ok();
        Assert.Equal(1, lenient.GetProperty("created").GetInt32());
        Assert.Equal(2, lenient.GetProperty("skipped").GetInt32());
        Assert.Equal(2, lenient.GetProperty("errors").GetArrayLength());
        Assert.Equal(1, (await admin.GetAsync($"/api/students?q={lenientNo}").Ok()).GetProperty("total").GetInt32());

        var strictNo = "S" + Seed.Tag();
        await Seed.ImportRoster(admin, Csv(strictNo), "?strict=true").Expect(422);
        Assert.Equal(0, (await admin.GetAsync($"/api/students?q={strictNo}").Ok()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Import_rejects_wrong_columns_and_a_missing_file()
    {
        var admin = await api.As("ADMIN");
        await admin.PostFileAsync("/api/students:import", "bad.csv", Encoding.UTF8.GetBytes("id,name\n1,Juan\n"), "text/csv").Expect(400);
        await admin.PostFileAsync("/api/students:import", "empty.csv", [], "text/csv").Expect(400);

        // Right file, wrong form field name: the server finds no "file" part.
        await admin.PostFileAsync("/api/students:import", "roster.csv", Encoding.UTF8.GetBytes(Seed.RosterCsv("X", ["1"])), "text/csv", field: "other").Expect(400);
    }

    // ------------------------------------------------------------ students

    [Fact]
    public async Task Student_list_filters_and_pages()
    {
        var admin = await api.As("ADMIN");
        var (sectionId, code) = await Seed.Section(admin);
        var nos = new[] { "S" + Seed.Tag(), "S" + Seed.Tag(), "S" + Seed.Tag() };
        await Seed.ImportRoster(admin, Seed.RosterCsv(code, nos)).Ok();

        var bySection = await admin.GetAsync($"/api/students?sectionId={sectionId}&schoolYear=2026-2027&status=ACTIVE&page=1&pageSize=2").Ok();
        Assert.Equal(3, bySection.GetProperty("total").GetInt32());
        Assert.Equal(2, bySection.GetProperty("items").GetArrayLength());

        var byName = await admin.GetAsync($"/api/students?q=First{nos[0]}").Ok();
        Assert.Equal(1, byName.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Student_status_and_standing_are_upper_snake_text()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);
        var body = await admin.GetAsync($"/api/students/{s.Id}").Ok();
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.Equal("GOOD", body.GetProperty("standing").GetString());
    }

    [Fact]
    public async Task Student_filter_with_a_wrong_enum_word_is_400()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync("/api/students?status=banana").Expect(400, "VALIDATION_FAILED");
        await admin.GetAsync("/api/students?status=1").Expect(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Student_update_replaces_the_name_and_moves_section()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);
        var (otherSection, _) = await Seed.Section(admin);

        var body = await admin.PutRawAsync($"/api/students/{s.Id}",
            $$"""{"studentNo":"{{s.StudentNo}}","firstName":"Juan","lastName":"Dela Cruz Jr.","sectionId":"{{otherSection}}"}""").Ok();
        Assert.Equal("Dela Cruz Jr.", body.GetProperty("lastName").GetString());
        Assert.Equal(otherSection, body.GetProperty("sectionId").GetGuid());
    }

    [Fact]
    public async Task Student_update_rejects_a_student_number_already_in_use()
    {
        var admin = await api.As("ADMIN");
        var a = await Seed.Student(admin);
        var b = await Seed.Student(admin);
        await admin.PutRawAsync($"/api/students/{b.Id}",
            $$"""{"studentNo":"{{a.StudentNo}}","firstName":"X","lastName":"Y","sectionId":"{{b.SectionId}}"}""").Expect(409);
    }

    [Fact]
    public async Task Student_soft_delete_hide_and_restore()
    {
        var admin = await api.As("ADMIN");
        var s = await Seed.Student(admin);

        await admin.DeleteAsync($"/api/students/{s.Id}").Expect(204);
        Assert.Equal(0, (await admin.GetAsync($"/api/students?q={s.StudentNo}").Ok()).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetAsync($"/api/students?q={s.StudentNo}&includeDeleted=true").Ok()).GetProperty("total").GetInt32());

        await admin.PostEmptyAsync($"/api/students/{s.Id}/restore").Expect(200);
        Assert.Equal(1, (await admin.GetAsync($"/api/students?q={s.StudentNo}").Ok()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Student_get_unknown_id_is_404()
    {
        var admin = await api.As("ADMIN");
        await admin.GetAsync($"/api/students/{Guid.NewGuid()}").Expect(404);
    }
}
