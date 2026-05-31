using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlackIce.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRealmMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Realms",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Realms");
        }
    }
}
