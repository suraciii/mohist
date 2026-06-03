using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations
{
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260603200000_AddWorkflowTemplateAndVariablesTables")]
    public partial class AddWorkflowTemplateAndVariablesTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "ProjectWorkflowProfiles" (
                    "ProjectId" TEXT NOT NULL PRIMARY KEY,
                    "DefaultTemplateId" TEXT,
                    "VariablesJson" TEXT NOT NULL DEFAULT '{}',
                    "UpdatedAt" TEXT NOT NULL DEFAULT '1900-01-01T00:00:00+00:00'
                );

                CREATE TABLE "ProjectTemplates" (
                    "ProjectId" TEXT NOT NULL,
                    "TemplateId" TEXT NOT NULL,
                    "TemplateJson" TEXT NOT NULL DEFAULT '{}',
                    "CreatedAt" TEXT NOT NULL DEFAULT '1900-01-01T00:00:00+00:00',
                    "UpdatedAt" TEXT NOT NULL DEFAULT '1900-01-01T00:00:00+00:00',
                    PRIMARY KEY ("ProjectId", "TemplateId")
                );

                CREATE INDEX "IX_ProjectTemplates_ProjectId" ON "ProjectTemplates" ("ProjectId");

                CREATE TABLE "IssueWorkflowProfiles" (
                    "IssueKey" TEXT NOT NULL PRIMARY KEY,
                    "SourceTemplateId" TEXT,
                    "TemplateJson" TEXT,
                    "VariablesJson" TEXT NOT NULL DEFAULT '{}',
                    "UpdatedAt" TEXT NOT NULL DEFAULT '1900-01-01T00:00:00+00:00'
                );

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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("WorkflowProfiles");
            migrationBuilder.DropTable("IssueWorkflowProfiles");
            migrationBuilder.DropTable("ProjectTemplates");
            migrationBuilder.DropTable("ProjectWorkflowProfiles");
        }
    }
}
