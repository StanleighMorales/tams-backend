using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LiabilityPerReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_liabilities_loan_id_uq",
                table: "liabilities");

            migrationBuilder.CreateIndex(
                name: "ix_liabilities_loan_id_reason_uq",
                table: "liabilities",
                columns: new[] { "loan_id", "reason" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_liabilities_loan_id_reason_uq",
                table: "liabilities");

            migrationBuilder.CreateIndex(
                name: "ix_liabilities_loan_id_uq",
                table: "liabilities",
                column: "loan_id",
                unique: true);
        }
    }
}
