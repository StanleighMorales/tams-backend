using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <summary>TAMS-120: a voided loan is CANCELLED and the ledger records a VOIDED row. EF only records enum labels in
    /// the model snapshot, so the labels are added to the native enums by hand.</summary>
    public partial class VoidLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TYPE loan_status ADD VALUE IF NOT EXISTS 'CANCELLED';");
            migrationBuilder.Sql("ALTER TYPE ledger_event ADD VALUE IF NOT EXISTS 'VOIDED';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL cannot drop an enum label; the unused labels are harmless.
        }
    }
}
