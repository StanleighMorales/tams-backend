using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <summary>
    /// TAMS-131 (G5): the checkout-photo purge keeps ledger and loan rows but removes the photo. So
    /// loans get photo_purged_at (which satisfies the STANDARD-loan photo check), and the ledger's
    /// append-only trigger allows exactly one kind of UPDATE: photo_file_id set to NULL, nothing else changed.
    /// </summary>
    [DbContext(typeof(TamsDbContext))]
    [Migration("20261116700000_RetentionPurge")]
    public partial class RetentionPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "photo_purged_at",
                table: "loans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.DropCheckConstraint(name: "ck_loans_standard_photo", table: "loans");
            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_standard_photo",
                table: "loans",
                sql: "type <> 'STANDARD' OR photo_file_id IS NOT NULL OR photo_purged_at IS NOT NULL");

            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION transaction_ledger_reject_change() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.photo_file_id IS NOT NULL AND NEW.photo_file_id IS NULL
       AND (to_jsonb(NEW) - 'photo_file_id') = (to_jsonb(OLD) - 'photo_file_id') THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'transaction_ledger is append-only';
END;
$$ LANGUAGE plpgsql;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION transaction_ledger_reject_change() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'transaction_ledger is append-only';
END;
$$ LANGUAGE plpgsql;");

            migrationBuilder.DropCheckConstraint(name: "ck_loans_standard_photo", table: "loans");
            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_standard_photo",
                table: "loans",
                sql: "type <> 'STANDARD' OR photo_file_id IS NOT NULL");

            migrationBuilder.DropColumn(name: "photo_purged_at", table: "loans");
        }
    }
}
