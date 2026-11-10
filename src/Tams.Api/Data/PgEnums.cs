using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Npgsql.NameTranslation;

namespace Tams.Api.Data;

// Registers every enum in Tams.Api.Data as a native PostgreSQL enum (PRD 7.5 uses CREATE TYPE).
// UserRole -> user_role, labels TechStaff -> TECH_STAFF.
public static class PgEnums
{
    static readonly Regex WordBreak = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    public static string ToSnake(string name) => WordBreak.Replace(name, "_").ToLowerInvariant();

    sealed class Translator : INpgsqlNameTranslator
    {
        public string TranslateMemberName(string clrName) => ToSnake(clrName).ToUpperInvariant();
        public string TranslateTypeName(string clrTypeName) => ToSnake(clrTypeName);
    }

    static readonly INpgsqlNameTranslator Names = new Translator();

    static IEnumerable<Type> EnumTypes() =>
        typeof(PgEnums).Assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == typeof(PgEnums).Namespace);

    // Driver side: tells Npgsql how to read and write the enums.
    public static void MapAll(NpgsqlDbContextOptionsBuilder npgsql)
    {
        var map = typeof(NpgsqlDbContextOptionsBuilder).GetMethods()
            .Single(m => m.Name == "MapEnum" && m.IsGenericMethodDefinition);
        foreach (var type in EnumTypes())
            map.MakeGenericMethod(type).Invoke(npgsql, [ToSnake(type.Name), null, Names]);
    }

    // Model side: makes migrations emit CREATE TYPE.
    public static void CreateAll(ModelBuilder modelBuilder)
    {
        foreach (var type in EnumTypes())
            modelBuilder.HasPostgresEnum(
                ToSnake(type.Name),
                Enum.GetNames(type).Select(Names.TranslateMemberName).ToArray());
    }
}
