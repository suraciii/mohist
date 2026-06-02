using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations
{
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260602102000_AddWorkflowQueue")]
    public partial class AddWorkflowQueue : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "WorkflowQueue" (
                    "WorkflowRunId" TEXT NOT NULL CONSTRAINT "PK_WorkflowQueue" PRIMARY KEY,
                    "ProjectId" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "RunnerId" TEXT NULL,
                    "WorkId" TEXT NULL,
                    "WorkType" TEXT NULL,
                    "Stage" TEXT NULL,
                    "LogicalId" TEXT NULL,
                    "Title" TEXT NULL,
                    "LeaseExpiresAt" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """);

            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_WorkflowQueue_LeaseExpiresAt" ON "WorkflowQueue" ("LeaseExpiresAt");""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_WorkflowQueue_ProjectId_State_UpdatedAt" ON "WorkflowQueue" ("ProjectId", "State", "UpdatedAt");""");
            migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_WorkflowQueue_RunnerId_State" ON "WorkflowQueue" ("RunnerId", "State");""");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowQueue");
        }
    }
}
