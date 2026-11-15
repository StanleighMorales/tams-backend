using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventBorrowerRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "borrower_id_no",
                table: "loans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "borrower_name",
                table: "loans",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "borrower_student_id",
                table: "loans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "student_id",
                table: "liabilities",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "liable_name",
                table: "liabilities",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "liable_user_id",
                table: "liabilities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_loans_borrower_student_id",
                table: "loans",
                column: "borrower_student_id");

            migrationBuilder.CreateIndex(
                name: "ix_liabilities_liable_user_id",
                table: "liabilities",
                column: "liable_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_liabilities_one_liable",
                table: "liabilities",
                sql: "num_nonnulls(student_id, liable_user_id, liable_name) = 1");

            migrationBuilder.AddForeignKey(
                name: "fk_liabilities_liable_user_id",
                table: "liabilities",
                column: "liable_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_loans_borrower_student_id",
                table: "loans",
                column: "borrower_student_id",
                principalTable: "students",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_liabilities_liable_user_id",
                table: "liabilities");

            migrationBuilder.DropForeignKey(
                name: "fk_loans_borrower_student_id",
                table: "loans");

            migrationBuilder.DropIndex(
                name: "ix_loans_borrower_student_id",
                table: "loans");

            migrationBuilder.DropIndex(
                name: "ix_liabilities_liable_user_id",
                table: "liabilities");

            migrationBuilder.DropCheckConstraint(
                name: "ck_liabilities_one_liable",
                table: "liabilities");

            migrationBuilder.DropColumn(
                name: "borrower_id_no",
                table: "loans");

            migrationBuilder.DropColumn(
                name: "borrower_name",
                table: "loans");

            migrationBuilder.DropColumn(
                name: "borrower_student_id",
                table: "loans");

            migrationBuilder.DropColumn(
                name: "liable_name",
                table: "liabilities");

            migrationBuilder.DropColumn(
                name: "liable_user_id",
                table: "liabilities");

            migrationBuilder.AlterColumn<Guid>(
                name: "student_id",
                table: "liabilities",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
