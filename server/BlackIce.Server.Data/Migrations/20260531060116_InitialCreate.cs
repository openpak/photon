using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlackIce.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    SteamId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBanned = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.SteamId);
                });

            migrationBuilder.CreateTable(
                name: "Realms",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Pvp = table.Column<bool>(type: "INTEGER", nullable: false),
                    HackDifficultyIncrease = table.Column<int>(type: "INTEGER", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false),
                    MaxPlayers = table.Column<int>(type: "INTEGER", nullable: false),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExtraJson = table.Column<string>(type: "TEXT", nullable: false),
                    Motd = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Realms", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "ServerState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BootstrapCode = table.Column<string>(type: "TEXT", nullable: true),
                    BootstrapClaimed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Motd = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    SteamId = table.Column<string>(type: "TEXT", nullable: false),
                    PlaytimeSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.SteamId);
                    table.ForeignKey(
                        name: "FK_Profiles_Accounts_SteamId",
                        column: x => x.SteamId,
                        principalTable: "Accounts",
                        principalColumn: "SteamId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Profiles");

            migrationBuilder.DropTable(
                name: "Realms");

            migrationBuilder.DropTable(
                name: "ServerState");

            migrationBuilder.DropTable(
                name: "Accounts");
        }
    }
}
