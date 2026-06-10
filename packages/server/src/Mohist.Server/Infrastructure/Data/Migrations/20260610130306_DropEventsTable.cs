using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropEventsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Type_Source_Id",
                table: "Events",
                columns: new[] { "Type", "Source", "Id" });
        }
    }
}
