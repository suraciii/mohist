using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Fixes the STORED computed columns on AgentSessions to use camelCase
    /// JSON paths ($.metadata.labels.*) matching the actual JSON keys written
    /// by JSON.Options (JsonSerializerDefaults.Web).
    ///
    /// SQLite does not support ALTER COLUMN on STORED computed columns, so
    /// we rebuild the table: create a copy with the corrected expressions,
    /// copy all data, drop the old table, rename the copy.
    /// </summary>
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260622000001_FixAgentSessionLabelsComputedColumnCase")]
    public partial class FixAgentSessionLabelsComputedColumnCase : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite cannot ALTER COLUMN a STORED computed column in place;
            // rebuild the table with corrected json_extract paths.
            // COALESCE both cases so historical PascalCase rows (written by
            // older code before JSON.Options was adopted) are not orphaned.
            migrationBuilder.Sql("""
                CREATE TABLE "AgentSessions_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentSessions" PRIMARY KEY,
                    "AgentSessionId" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "LabelIssueNumber" AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/issue-number"'),  json_extract("State", '$.Metadata.Labels."mohist.io/issue-number"'))) STORED,
                    "LabelProjectId"   AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/project-id"'),    json_extract("State", '$.Metadata.Labels."mohist.io/project-id"'))) STORED,
                    "LabelSessionName" AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/session-name"'),  json_extract("State", '$.Metadata.Labels."mohist.io/session-name"'))) STORED,
                    "LabelSourceId"    AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/source-id"'),     json_extract("State", '$.Metadata.Labels."mohist.io/source-id"'))) STORED,
                    "LabelSourceKind"  AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/source-kind"'),   json_extract("State", '$.Metadata.Labels."mohist.io/source-kind"'))) STORED,
                    "LabelStage"       AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/stage"'),         json_extract("State", '$.Metadata.Labels."mohist.io/stage"'))) STORED,
                    "LabelWorkId"      AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/work-id"'),       json_extract("State", '$.Metadata.Labels."mohist.io/work-id"'))) STORED,
                    "LabelWorkType"    AS (COALESCE(json_extract("State", '$.metadata.labels."mohist.io/work-type"'),     json_extract("State", '$.Metadata.Labels."mohist.io/work-type"'))) STORED,
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
