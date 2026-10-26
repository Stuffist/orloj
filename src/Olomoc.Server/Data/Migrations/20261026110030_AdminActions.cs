using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olomoc.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Admin = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminActions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminActions_At",
                table: "AdminActions",
                column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminActions");
        }
    }
}
