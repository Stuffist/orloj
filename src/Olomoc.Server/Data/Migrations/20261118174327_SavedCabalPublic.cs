using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olomoc.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavedCabalPublic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Public",
                table: "SavedCabals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Public",
                table: "SavedCabals");
        }
    }
}
