using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olomoc.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ZoneGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ZoneGames",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ZoneCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PlayedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmitterId = table.Column<string>(type: "TEXT", nullable: true),
                    EditTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneGames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZoneGames_AspNetUsers_SubmitterId",
                        column: x => x.SubmitterId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ZoneGames_SubmitterId",
                table: "ZoneGames",
                column: "SubmitterId");

            migrationBuilder.CreateIndex(
                name: "IX_ZoneGames_ZoneCode_Id",
                table: "ZoneGames",
                columns: new[] { "ZoneCode", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ZoneGames");
        }
    }
}
