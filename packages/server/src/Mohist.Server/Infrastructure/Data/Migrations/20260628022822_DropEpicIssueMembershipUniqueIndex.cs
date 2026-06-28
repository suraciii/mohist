using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Issue-179: relax the historical membership index and add a dedicated
    /// active-membership slot keyed by issue. Terminal history can coexist
    /// with a new active owner, while the database still hard-enforces at most
    /// one non-terminal epic membership per issue.
    /// </summary>
    public partial class DropEpicIssueMembershipUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues");

            migrationBuilder.CreateIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues",
                columns: new[] { "ProjectId", "IssueId" });

            migrationBuilder.CreateTable(
                name: "EpicActiveIssues",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EpicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicActiveIssues", x => new { x.ProjectId, x.IssueId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpicActiveIssues_ProjectId_EpicId",
                table: "EpicActiveIssues",
                columns: new[] { "ProjectId", "EpicId" });

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
                SELECT li."ProjectId", li."IssueId", li."EpicId", li."IssueNumber", li."CreatedAt"
                FROM "EpicIssues" li
                INNER JOIN "Epics" e ON e."Id" = li."EpicId" AND e."ProjectId" = li."ProjectId"
                WHERE e."Status" NOT IN ('done', 'closed')
                ORDER BY li."CreatedAt", li."EpicId"
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "EpicActiveIssues");

            migrationBuilder.DropIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues");

            migrationBuilder.CreateIndex(
                name: "IX_EpicIssues_ProjectId_IssueId",
                table: "EpicIssues",
                columns: new[] { "ProjectId", "IssueId" },
                unique: true);
        }
    }
}
