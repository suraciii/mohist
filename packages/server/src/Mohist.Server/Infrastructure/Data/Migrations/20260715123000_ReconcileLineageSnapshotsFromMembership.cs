using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260715123000_ReconcileLineageSnapshotsFromMembership")]
    public partial class ReconcileLineageSnapshotsFromMembership : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "Issues"
                SET "EpicId" = (
                        SELECT "EpicId"
                        FROM (
                            SELECT a."EpicId", 0 AS "Priority", a."CreatedAt"
                            FROM "EpicActiveIssues" a
                            WHERE a."ProjectId" = "Issues"."ProjectId"
                              AND a."IssueId" = "Issues"."IssueId"
                            UNION ALL
                            SELECT l."EpicId", 1 AS "Priority", l."CreatedAt"
                            FROM "EpicIssues" l
                            WHERE l."ProjectId" = "Issues"."ProjectId"
                              AND l."IssueId" = "Issues"."IssueId"
                        )
                        ORDER BY "Priority", "CreatedAt" DESC, "EpicId"
                        LIMIT 1),
                    "State" = json_set("State", '$.epicId', (
                        SELECT "EpicId"
                        FROM (
                            SELECT a."EpicId", 0 AS "Priority", a."CreatedAt"
                            FROM "EpicActiveIssues" a
                            WHERE a."ProjectId" = "Issues"."ProjectId"
                              AND a."IssueId" = "Issues"."IssueId"
                            UNION ALL
                            SELECT l."EpicId", 1 AS "Priority", l."CreatedAt"
                            FROM "EpicIssues" l
                            WHERE l."ProjectId" = "Issues"."ProjectId"
                              AND l."IssueId" = "Issues"."IssueId"
                        )
                        ORDER BY "Priority", "CreatedAt" DESC, "EpicId"
                        LIMIT 1)),
                    "LineageVersion" = "LineageVersion" + 1;

                UPDATE "WorkflowRuns"
                SET "EpicId" = CASE
                    WHEN EXISTS (SELECT 1 FROM "Issues"
                        WHERE "IssueId" = COALESCE(
                            json_extract("WorkflowRuns"."State", '$.metadata.annotations.issueId'),
                            json_extract("WorkflowRuns"."State", '$.Metadata.Annotations.issueId'),
                            json_extract("WorkflowRuns"."State", '$.Metadata.Annotations.IssueId')))
                    THEN (SELECT "EpicId" FROM "Issues"
                        WHERE "IssueId" = COALESCE(
                            json_extract("WorkflowRuns"."State", '$.metadata.annotations.issueId'),
                            json_extract("WorkflowRuns"."State", '$.Metadata.Annotations.issueId'),
                            json_extract("WorkflowRuns"."State", '$.Metadata.Annotations.IssueId')))
                    ELSE NULLIF(TRIM(COALESCE(
                        json_extract("State", '$.metadata.annotations.epicId'),
                        json_extract("State", '$.Metadata.Annotations.epicId'),
                        json_extract("State", '$.Metadata.Annotations.EpicId'))), '')
                END;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
