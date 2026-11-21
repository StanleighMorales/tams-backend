using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FingerprintTemplateFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "template_format",
                table: "fingerprint_templates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValueSql: "'RAW_V1'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "template_format",
                table: "fingerprint_templates");
        }
    }
}
