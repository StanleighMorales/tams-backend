using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Tams.Api.Data;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SchemaV1Conformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_email_uq",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_employee_no_uq",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_normalized_user_name_uq",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_students_student_no_uq",
                table: "students");

            migrationBuilder.DropIndex(
                name: "ix_stations_station_code_uq",
                table: "stations");

            migrationBuilder.DropIndex(
                name: "ix_rooms_code_uq",
                table: "rooms");

            migrationBuilder.DropIndex(
                name: "ix_assets_asset_code_uq",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_rfid_tag_uid_uq",
                table: "assets");

            migrationBuilder.AlterColumn<bool>(
                name: "is_event_coordinator",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false",
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<StudentStanding>(
                name: "standing",
                table: "students",
                type: "student_standing",
                nullable: false,
                defaultValueSql: "'GOOD'::student_standing",
                oldClrType: typeof(StudentStanding),
                oldType: "student_standing");

            migrationBuilder.AlterColumn<bool>(
                name: "has_active_hold",
                table: "students",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false",
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<short>(
                name: "active_warning_count",
                table: "students",
                type: "smallint",
                nullable: false,
                defaultValueSql: "0",
                oldClrType: typeof(short),
                oldType: "smallint");

            migrationBuilder.AlterColumn<short>(
                name: "active_mark_count",
                table: "students",
                type: "smallint",
                nullable: false,
                defaultValueSql: "0",
                oldClrType: typeof(short),
                oldType: "smallint");

            migrationBuilder.AlterColumn<StationHealth>(
                name: "health",
                table: "stations",
                type: "station_health",
                nullable: false,
                defaultValueSql: "'OFFLINE'::station_health",
                oldClrType: typeof(StationHealth),
                oldType: "station_health");

            migrationBuilder.AlterColumn<string>(
                name: "building",
                table: "rooms",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValueSql: "'College Building'",
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "lcd_lines",
                table: "checkout_sessions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<short>(
                name: "fingerprint_attempts",
                table: "checkout_sessions",
                type: "smallint",
                nullable: false,
                defaultValueSql: "0",
                oldClrType: typeof(short),
                oldType: "smallint");

            migrationBuilder.AddColumn<short>(
                name: "finger_index",
                table: "checkout_sessions",
                type: "smallint",
                nullable: true);

            migrationBuilder.AlterColumn<AssetStatus>(
                name: "status",
                table: "assets",
                type: "asset_status",
                nullable: false,
                defaultValueSql: "'IN_STORAGE'::asset_status",
                oldClrType: typeof(AssetStatus),
                oldType: "asset_status");

            migrationBuilder.AlterColumn<AssetCondition>(
                name: "condition",
                table: "assets",
                type: "asset_condition",
                nullable: false,
                defaultValueSql: "'GOOD'::asset_condition",
                oldClrType: typeof(AssetCondition),
                oldType: "asset_condition");

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_replaced_by_id",
                        column: x => x.replaced_by_id,
                        principalTable: "refresh_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_email_uq",
                table: "users",
                column: "email",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_employee_no_uq",
                table: "users",
                column: "employee_no",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_user_name_uq",
                table: "users",
                column: "normalized_user_name",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_students_student_no_uq",
                table: "students",
                column: "student_no",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stations_station_code_uq",
                table: "stations",
                column: "station_code",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rooms_code_uq",
                table: "rooms",
                column: "code",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loans_standard_photo",
                table: "loans",
                sql: "type <> 'STANDARD' OR photo_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_checkout_sessions_open_enrollment_student",
                table: "checkout_sessions",
                column: "student_id",
                unique: true,
                filter: "type = 'ENROLLMENT' AND status IN ('AWAITING_FINGERPRINT','AWAITING_ASSET','AWAITING_PHOTO','AWAITING_APPROVAL','AWAITING_CONFIRM')");

            migrationBuilder.CreateIndex(
                name: "ux_checkout_sessions_open_station",
                table: "checkout_sessions",
                column: "station_id",
                unique: true,
                filter: "status IN ('AWAITING_FINGERPRINT','AWAITING_ASSET','AWAITING_PHOTO','AWAITING_APPROVAL','AWAITING_CONFIRM')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_checkout_sessions_finger_index",
                table: "checkout_sessions",
                sql: "finger_index IS NULL OR finger_index BETWEEN 0 AND 9");

            migrationBuilder.CreateIndex(
                name: "ix_assets_asset_code_uq",
                table: "assets",
                column: "asset_code",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_assets_rfid_tag_uid_uq",
                table: "assets",
                column: "rfid_tag_uid",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_borrowable_tag",
                table: "assets",
                sql: "type <> 'BORROWABLE' OR rfid_tag_uid IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_replaced_by_id",
                table: "refresh_tokens",
                column: "replaced_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash_uq",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "ix_users_email_uq",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_employee_no_uq",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_normalized_user_name_uq",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_students_student_no_uq",
                table: "students");

            migrationBuilder.DropIndex(
                name: "ix_stations_station_code_uq",
                table: "stations");

            migrationBuilder.DropIndex(
                name: "ix_rooms_code_uq",
                table: "rooms");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loans_standard_photo",
                table: "loans");

            migrationBuilder.DropIndex(
                name: "ux_checkout_sessions_open_enrollment_student",
                table: "checkout_sessions");

            migrationBuilder.DropIndex(
                name: "ux_checkout_sessions_open_station",
                table: "checkout_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_checkout_sessions_finger_index",
                table: "checkout_sessions");

            migrationBuilder.DropIndex(
                name: "ix_assets_asset_code_uq",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_rfid_tag_uid_uq",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_borrowable_tag",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "finger_index",
                table: "checkout_sessions");

            migrationBuilder.AlterColumn<bool>(
                name: "is_event_coordinator",
                table: "users",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValueSql: "false");

            migrationBuilder.AlterColumn<StudentStanding>(
                name: "standing",
                table: "students",
                type: "student_standing",
                nullable: false,
                oldClrType: typeof(StudentStanding),
                oldType: "student_standing",
                oldDefaultValueSql: "'GOOD'::student_standing");

            migrationBuilder.AlterColumn<bool>(
                name: "has_active_hold",
                table: "students",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValueSql: "false");

            migrationBuilder.AlterColumn<short>(
                name: "active_warning_count",
                table: "students",
                type: "smallint",
                nullable: false,
                oldClrType: typeof(short),
                oldType: "smallint",
                oldDefaultValueSql: "0");

            migrationBuilder.AlterColumn<short>(
                name: "active_mark_count",
                table: "students",
                type: "smallint",
                nullable: false,
                oldClrType: typeof(short),
                oldType: "smallint",
                oldDefaultValueSql: "0");

            migrationBuilder.AlterColumn<StationHealth>(
                name: "health",
                table: "stations",
                type: "station_health",
                nullable: false,
                oldClrType: typeof(StationHealth),
                oldType: "station_health",
                oldDefaultValueSql: "'OFFLINE'::station_health");

            migrationBuilder.AlterColumn<string>(
                name: "building",
                table: "rooms",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80,
                oldDefaultValueSql: "'College Building'");

            migrationBuilder.AlterColumn<string>(
                name: "lcd_lines",
                table: "checkout_sessions",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'::jsonb");

            migrationBuilder.AlterColumn<short>(
                name: "fingerprint_attempts",
                table: "checkout_sessions",
                type: "smallint",
                nullable: false,
                oldClrType: typeof(short),
                oldType: "smallint",
                oldDefaultValueSql: "0");

            migrationBuilder.AlterColumn<AssetStatus>(
                name: "status",
                table: "assets",
                type: "asset_status",
                nullable: false,
                oldClrType: typeof(AssetStatus),
                oldType: "asset_status",
                oldDefaultValueSql: "'IN_STORAGE'::asset_status");

            migrationBuilder.AlterColumn<AssetCondition>(
                name: "condition",
                table: "assets",
                type: "asset_condition",
                nullable: false,
                oldClrType: typeof(AssetCondition),
                oldType: "asset_condition",
                oldDefaultValueSql: "'GOOD'::asset_condition");

            migrationBuilder.CreateIndex(
                name: "ix_users_email_uq",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_employee_no_uq",
                table: "users",
                column: "employee_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_user_name_uq",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_students_student_no_uq",
                table: "students",
                column: "student_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stations_station_code_uq",
                table: "stations",
                column: "station_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rooms_code_uq",
                table: "rooms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_asset_code_uq",
                table: "assets",
                column: "asset_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_rfid_tag_uid_uq",
                table: "assets",
                column: "rfid_tag_uid",
                unique: true);
        }
    }
}
