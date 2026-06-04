using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260604090000_AddWorkflowProfileCurrentColumns")]
public partial class AddWorkflowProfileCurrentColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ProjectWorkflowProfiles" ADD COLUMN "Variables" TEXT NOT NULL DEFAULT '{}';
            UPDATE "ProjectWorkflowProfiles" SET "Variables" = COALESCE("VariablesJson", '{}');
            ALTER TABLE "ProjectWorkflowProfiles" ADD COLUMN "Prompts" TEXT NOT NULL DEFAULT '{}';
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "IssueWorkflowProfiles" ADD COLUMN "Template" TEXT;
            UPDATE "IssueWorkflowProfiles" SET "Template" = "TemplateJson";
            ALTER TABLE "IssueWorkflowProfiles" ADD COLUMN "Variables" TEXT NOT NULL DEFAULT '{}';
            UPDATE "IssueWorkflowProfiles" SET "Variables" = COALESCE("VariablesJson", '{}');
            ALTER TABLE "IssueWorkflowProfiles" ADD COLUMN "Prompts" TEXT NOT NULL DEFAULT '{}';
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "ProjectTemplates" ADD COLUMN "Template" TEXT NOT NULL DEFAULT '{}';
            UPDATE "ProjectTemplates" SET "Template" = COALESCE("TemplateJson", '{}');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
