using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Persistence.Db;

#nullable disable

namespace Mohist.Server.Migrations
{
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260602140000_AddWorkflowRunETagAndAdoNetReminders")]
    public partial class AddWorkflowRunETagAndAdoNetReminders : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "__temp_workflow_runs" (
                    "WorkflowRunId" TEXT NOT NULL CONSTRAINT "PK_workflow_runs" PRIMARY KEY,
                    "State" TEXT NOT NULL,
                    "ETag" INTEGER NOT NULL DEFAULT 1,
                    "MetadataProjectId" AS (json_extract(State, '$.Metadata.Annotations.projectId')) STORED
                );

                INSERT INTO "__temp_workflow_runs" ("WorkflowRunId", "State", "ETag")
                SELECT "WorkflowRunId", "State", 1
                FROM "workflow_runs";

                DROP TABLE "workflow_runs";

                ALTER TABLE "__temp_workflow_runs" RENAME TO "workflow_runs";

                CREATE INDEX "IX_workflow_runs_MetadataProjectId" ON "workflow_runs" ("MetadataProjectId");
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "OrleansQuery" (
                    "QueryKey" TEXT NOT NULL PRIMARY KEY,
                    "QueryText" TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS "OrleansRemindersTable" (
                    "ServiceId" TEXT NOT NULL,
                    "GrainId" TEXT NOT NULL,
                    "ReminderName" TEXT NOT NULL,
                    "StartTime" TEXT NOT NULL,
                    "Period" INTEGER NOT NULL,
                    "GrainHash" INTEGER NOT NULL,
                    "Version" INTEGER NOT NULL,
                    CONSTRAINT "PK_OrleansRemindersTable" PRIMARY KEY ("ServiceId", "GrainId", "ReminderName")
                );
                """);

            UpsertOrleansQuery(migrationBuilder, "ReadReminderRowsKey", """
                SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainId = @GrainId AND @GrainId IS NOT NULL;
                """);

            UpsertOrleansQuery(migrationBuilder, "ReadReminderRowKey", """
                SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainId = @GrainId AND @GrainId IS NOT NULL
                    AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL;
                """);

            UpsertOrleansQuery(migrationBuilder, "ReadRangeRows1Key", """
                SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainHash > @BeginHash AND @BeginHash IS NOT NULL
                    AND GrainHash <= @EndHash AND @EndHash IS NOT NULL;
                """);

            UpsertOrleansQuery(migrationBuilder, "ReadRangeRows2Key", """
                SELECT
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    Version
                FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND ((GrainHash > @BeginHash AND @BeginHash IS NOT NULL)
                    OR (GrainHash <= @EndHash AND @EndHash IS NOT NULL));
                """);

            UpsertOrleansQuery(migrationBuilder, "UpsertReminderRowKey", """
                INSERT INTO OrleansRemindersTable
                (
                    ServiceId,
                    GrainId,
                    ReminderName,
                    StartTime,
                    Period,
                    GrainHash,
                    Version
                )
                VALUES
                (
                    @ServiceId,
                    @GrainId,
                    @ReminderName,
                    @StartTime,
                    @Period,
                    @GrainHash,
                    0
                )
                ON CONFLICT(ServiceId, GrainId, ReminderName) DO UPDATE SET
                    StartTime = excluded.StartTime,
                    Period = excluded.Period,
                    GrainHash = excluded.GrainHash,
                    Version = OrleansRemindersTable.Version + 1
                RETURNING Version;
                """);

            UpsertOrleansQuery(migrationBuilder, "DeleteReminderRowKey", """
                DELETE FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND GrainId = @GrainId AND @GrainId IS NOT NULL
                    AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
                    AND Version = @Version AND @Version IS NOT NULL
                RETURNING 1;
                """);

            UpsertOrleansQuery(migrationBuilder, "DeleteReminderRowsKey", """
                DELETE FROM OrleansRemindersTable
                WHERE
                    ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "OrleansRemindersTable");
            migrationBuilder.DropTable(name: "OrleansQuery");
            migrationBuilder.Sql("""
                CREATE TABLE "__temp_workflow_runs" (
                    "WorkflowRunId" TEXT NOT NULL CONSTRAINT "PK_workflow_runs" PRIMARY KEY,
                    "State" TEXT NOT NULL,
                    "MetadataProjectId" AS (json_extract(State, '$.Metadata.Annotations.projectId')) STORED
                );

                INSERT INTO "__temp_workflow_runs" ("WorkflowRunId", "State")
                SELECT "WorkflowRunId", "State"
                FROM "workflow_runs";

                DROP TABLE "workflow_runs";

                ALTER TABLE "__temp_workflow_runs" RENAME TO "workflow_runs";

                CREATE INDEX "IX_workflow_runs_MetadataProjectId" ON "workflow_runs" ("MetadataProjectId");
                """);
        }

        private static void UpsertOrleansQuery(MigrationBuilder migrationBuilder, string key, string query)
        {
            var escapedQuery = query.Replace("'", "''");
            migrationBuilder.Sql($"""
                INSERT INTO "OrleansQuery" ("QueryKey", "QueryText")
                VALUES ('{key}', '{escapedQuery}')
                ON CONFLICT("QueryKey") DO UPDATE SET "QueryText" = excluded."QueryText";
                """);
        }
    }
}
