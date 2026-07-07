using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSubscriptionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSubscriptions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FilterType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FilterSource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    FilterSubject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ResponsePrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSubscriptions_ProjectId",
                table: "AgentSubscriptions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSubscriptions_ProjectId_AgentId",
                table: "AgentSubscriptions",
                columns: new[] { "ProjectId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "UX_AgentSubscriptions_AgentId_Name",
                table: "AgentSubscriptions",
                columns: new[] { "AgentId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSubscriptions");
        }
    }
}
