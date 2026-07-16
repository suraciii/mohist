using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260716130000_AddCanonicalIssueReferenceColumns")]
public partial class AddCanonicalIssueReferenceColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProjectId",
            table: "IssueWorkflowProfiles",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "IssueNumber",
            table: "IssueWorkflowProfiles",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "IssueNumber",
            table: "WorkflowArtifacts",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "OwnerIssueNumber",
            table: "Attachments",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "IssueNumber",
            table: "WorkflowRuns",
            type: "INTEGER",
            nullable: true,
            computedColumnSql: "CAST(COALESCE(json_extract(State, '$.metadata.annotations.issueNumber'), json_extract(State, '$.Metadata.Annotations.issueNumber'), json_extract(State, '$.Metadata.Annotations.IssueNumber')) AS INTEGER)",
            stored: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IssueNumber", table: "WorkflowRuns");
        migrationBuilder.DropColumn(name: "OwnerIssueNumber", table: "Attachments");
        migrationBuilder.DropColumn(name: "IssueNumber", table: "WorkflowArtifacts");
        migrationBuilder.DropColumn(name: "IssueNumber", table: "IssueWorkflowProfiles");
        migrationBuilder.DropColumn(name: "ProjectId", table: "IssueWorkflowProfiles");
    }
}
