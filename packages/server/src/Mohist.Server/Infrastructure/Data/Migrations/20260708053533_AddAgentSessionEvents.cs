using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSessionEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessionEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionEvents_Type_Source_Id",
                table: "AgentSessionEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionEvents_Undelivered",
                table: "AgentSessionEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSessionEvents");
        }
    }
}
