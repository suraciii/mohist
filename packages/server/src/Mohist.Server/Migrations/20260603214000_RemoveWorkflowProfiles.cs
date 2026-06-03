using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260603214000_RemoveWorkflowProfiles")]
public partial class RemoveWorkflowProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "WorkflowProfiles";""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "WorkflowProfiles" (
                "WorkflowRunId" TEXT NOT NULL PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "IssueKey" TEXT NOT NULL,
                "TemplateJson" TEXT NOT NULL DEFAULT '{}',
                "VariablesJson" TEXT NOT NULL DEFAULT '{}',
                "CreatedAt" TEXT NOT NULL DEFAULT '1900-01-01T00:00:00+00:00',
                "UpdatedAt" TEXT NOT NULL DEFAULT '1900-01-01T00:00:00+00:00'
            );

            CREATE INDEX "IX_WorkflowProfiles_ProjectId" ON "WorkflowProfiles" ("ProjectId");
            CREATE INDEX "IX_WorkflowProfiles_IssueKey" ON "WorkflowProfiles" ("IssueKey");
            """);
    }
}
