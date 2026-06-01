using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Migrations
{
    public partial class AddWorkflowStageLocks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "WorkflowStageLocks" (
                    "Key" TEXT NOT NULL CONSTRAINT "PK_WorkflowStageLocks" PRIMARY KEY,
                    "StateJson" TEXT NOT NULL
                );
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowStageLocks");
        }
    }
}
