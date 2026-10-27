using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olomoc.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ZoneGameTournament : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TournamentCode",
                table: "ZoneGames",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TournamentTable",
                table: "ZoneGames",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZoneGames_ZoneCode_TournamentCode",
                table: "ZoneGames",
                columns: new[] { "ZoneCode", "TournamentCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ZoneGames_ZoneCode_TournamentCode",
                table: "ZoneGames");

            migrationBuilder.DropColumn(
                name: "TournamentCode",
                table: "ZoneGames");

            migrationBuilder.DropColumn(
                name: "TournamentTable",
                table: "ZoneGames");
        }
    }
}
