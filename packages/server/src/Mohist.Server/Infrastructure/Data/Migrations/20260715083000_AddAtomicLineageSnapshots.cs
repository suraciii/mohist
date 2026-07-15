using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    [DbContext(typeof(Db.MohistDbContext))]
    [Migration("20260715083000_AddAtomicLineageSnapshots")]
    public partial class AddAtomicLineageSnapshots : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpicId",
                table: "Issues",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpicId",
                table: "WorkflowRuns",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Issues"
                SET "EpicId" = NULLIF(TRIM(json_extract("State", '$.epicId')), '')
                WHERE "EpicId" IS NULL;

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
                END
                WHERE "EpicId" IS NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpicId",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "EpicId",
                table: "WorkflowRuns");
        }
    }
}
