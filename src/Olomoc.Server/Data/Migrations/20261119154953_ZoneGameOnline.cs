using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olomoc.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ZoneGameOnline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OnlineCode",
                table: "ZoneGames",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZoneGames_OnlineCode",
                table: "ZoneGames",
                column: "OnlineCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ZoneGames_OnlineCode",
                table: "ZoneGames");

            migrationBuilder.DropColumn(
                name: "OnlineCode",
                table: "ZoneGames");
        }
    }
}
