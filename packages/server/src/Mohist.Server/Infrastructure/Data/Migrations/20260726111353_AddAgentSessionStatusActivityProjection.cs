using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddAgentSessionStatusActivityProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "AgentSessions" ADD COLUMN "Activity" TEXT
            GENERATED ALWAYS AS (
                LOWER(COALESCE(
                    json_extract("State", '$.status.activity'),
                    json_extract("State", '$.status.Activity')))
            ) VIRTUAL;
            """);

        migrationBuilder.Sql("""
            CREATE INDEX "IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt"
            ON "AgentSessions" ("LabelProjectId", "LabelSourceKind", "Activity", "CreatedAt");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt\";");
        migrationBuilder.Sql("ALTER TABLE \"AgentSessions\" DROP COLUMN \"Activity\";");
    }
}
