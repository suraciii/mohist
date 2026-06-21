using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Rebuilds AgentSessions STORED computed columns with camelCase
    /// json_extract paths ($.metadata.labels.*) to match the JSON keys
    /// written by JSON.Options (JsonSerializerDefaults.Web).
    /// SQLite does not support ALTER COLUMN on STORED computed columns,
    /// so the table is rebuilt via raw SQL (create-copy-drop-rename).
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260622000001_FixAgentSessionLabelsComputedColumnCase")]
    public partial class FixAgentSessionLabelsComputedColumnCase : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "AgentSessions_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentSessions" PRIMARY KEY,
                    "AgentSessionId" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "LabelIssueNumber" AS (json_extract("State", '$.metadata.labels."mohist.io/issue-number"')) STORED,
                    "LabelProjectId"   AS (json_extract("State", '$.metadata.labels."mohist.io/project-id"')) STORED,
                    "LabelSessionName" AS (json_extract("State", '$.metadata.labels."mohist.io/session-name"')) STORED,
                    "LabelSourceId"    AS (json_extract("State", '$.metadata.labels."mohist.io/source-id"')) STORED,
                    "LabelSourceKind"  AS (json_extract("State", '$.metadata.labels."mohist.io/source-kind"')) STORED,
                    "LabelStage"       AS (json_extract("State", '$.metadata.labels."mohist.io/stage"')) STORED,
                    "LabelWorkId"      AS (json_extract("State", '$.metadata.labels."mohist.io/work-id"')) STORED,
                    "LabelWorkType"    AS (json_extract("State", '$.metadata.labels."mohist.io/work-type"')) STORED,
                    "LastDataAt" TEXT NULL,
                    "RunnerId" TEXT NULL,
                    "State" TEXT NOT NULL,
                    "Status" TEXT NOT NULL
                );

                INSERT INTO "AgentSessions_new" ("Id", "AgentSessionId", "CreatedAt", "LastDataAt", "RunnerId", "State", "Status")
                SELECT "Id", "AgentSessionId", "CreatedAt", "LastDataAt", "RunnerId", "State", "Status"
                FROM "AgentSessions";

                DROP TABLE "AgentSessions";
                ALTER TABLE "AgentSessions_new" RENAME TO "AgentSessions";

                CREATE INDEX "IX_AgentSessions_AgentSessionId" ON "AgentSessions" ("AgentSessionId");
                CREATE INDEX "IX_AgentSessions_LabelProjectId_CreatedAt" ON "AgentSessions" ("LabelProjectId", "CreatedAt");
                CREATE INDEX "IX_AgentSessions_LabelSourceId" ON "AgentSessions" ("LabelSourceId");
                CREATE INDEX "IX_AgentSessions_LabelSourceId_LabelSessionName" ON "AgentSessions" ("LabelSourceId", "LabelSessionName");
                CREATE INDEX "IX_AgentSessions_Status_CreatedAt" ON "AgentSessions" ("Status", "CreatedAt");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "AgentSessions_old" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentSessions" PRIMARY KEY,
                    "AgentSessionId" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "LabelIssueNumber" AS (json_extract("State", '$.Metadata.Labels."mohist.io/issue-number"')) STORED,
                    "LabelProjectId"   AS (json_extract("State", '$.Metadata.Labels."mohist.io/project-id"')) STORED,
                    "LabelSessionName" AS (json_extract("State", '$.Metadata.Labels."mohist.io/session-name"')) STORED,
                    "LabelSourceId"    AS (json_extract("State", '$.Metadata.Labels."mohist.io/source-id"')) STORED,
                    "LabelSourceKind"  AS (json_extract("State", '$.Metadata.Labels."mohist.io/source-kind"')) STORED,
                    "LabelStage"       AS (json_extract("State", '$.Metadata.Labels."mohist.io/stage"')) STORED,
                    "LabelWorkId"      AS (json_extract("State", '$.Metadata.Labels."mohist.io/work-id"')) STORED,
                    "LabelWorkType"    AS (json_extract("State", '$.Metadata.Labels."mohist.io/work-type"')) STORED,
                    "LastDataAt" TEXT NULL,
                    "RunnerId" TEXT NULL,
                    "State" TEXT NOT NULL,
                    "Status" TEXT NOT NULL
                );

                INSERT INTO "AgentSessions_old" ("Id", "AgentSessionId", "CreatedAt", "LastDataAt", "RunnerId", "State", "Status")
                SELECT "Id", "AgentSessionId", "CreatedAt", "LastDataAt", "RunnerId", "State", "Status"
                FROM "AgentSessions";

                DROP TABLE "AgentSessions";
                ALTER TABLE "AgentSessions_old" RENAME TO "AgentSessions";

                CREATE INDEX "IX_AgentSessions_AgentSessionId" ON "AgentSessions" ("AgentSessionId");
                CREATE INDEX "IX_AgentSessions_LabelProjectId_CreatedAt" ON "AgentSessions" ("LabelProjectId", "CreatedAt");
                CREATE INDEX "IX_AgentSessions_LabelSourceId" ON "AgentSessions" ("LabelSourceId");
                CREATE INDEX "IX_AgentSessions_LabelSourceId_LabelSessionName" ON "AgentSessions" ("LabelSourceId", "LabelSessionName");
                CREATE INDEX "IX_AgentSessions_Status_CreatedAt" ON "AgentSessions" ("Status", "CreatedAt");
                """);
        }
    }
}
