using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tams.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActiveCardUidUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_auth_cards_card_uid_uq",
                table: "auth_cards");

            migrationBuilder.CreateIndex(
                name: "ix_auth_cards_card_uid_uq",
                table: "auth_cards",
                column: "card_uid",
                unique: true,
                filter: "status = 'ACTIVE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_auth_cards_card_uid_uq",
                table: "auth_cards");

            migrationBuilder.CreateIndex(
                name: "ix_auth_cards_card_uid_uq",
                table: "auth_cards",
                column: "card_uid",
                unique: true);
        }
    }
}
