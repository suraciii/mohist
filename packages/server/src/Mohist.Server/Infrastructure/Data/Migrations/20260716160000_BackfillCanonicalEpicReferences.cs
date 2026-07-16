using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260716160000_BackfillCanonicalEpicReferences")]
public partial class BackfillCanonicalEpicReferences : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        MohistDbContextModelSnapshot.BuildModelCore(modelBuilder);

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(CanonicalEpicReferenceReconciliation.Sql);

        migrationBuilder.AlterColumn<int>(
            name: "Number",
            table: "Epics",
            type: "INTEGER",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "EpicNumber",
            table: "EpicIssues",
            type: "INTEGER",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "EpicNumber",
            table: "EpicActiveIssues",
            type: "INTEGER",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true);

        migrationBuilder.DropIndex(
            name: "IX_Epics_ProjectId_Number",
            table: "Epics");

        migrationBuilder.CreateIndex(
            name: "IX_Epics_ProjectId_Number",
            table: "Epics",
            columns: new[] { "ProjectId", "Number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_EpicIssues_ProjectId_EpicNumber_IssueNumber",
            table: "EpicIssues",
            columns: new[] { "ProjectId", "EpicNumber", "IssueNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_EpicActiveIssues_ProjectId_EpicNumber_IssueNumber",
            table: "EpicActiveIssues",
            columns: new[] { "ProjectId", "EpicNumber", "IssueNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_Issues_ProjectId_EpicNumber_Number",
            table: "Issues",
            columns: new[] { "ProjectId", "EpicNumber", "Number" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRuns_ProjectId_EpicNumber",
            table: "WorkflowRuns",
            columns: new[] { "MetadataProjectId", "EpicNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelAgentLaunchEpicNumber_CreatedAt",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "LabelAgentLaunchEpicNumber", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelProjectId_LabelAgentLaunchEpicNumber_CreatedAt",
            table: "AgentSessions");
        migrationBuilder.DropIndex(name: "IX_WorkflowRuns_ProjectId_EpicNumber", table: "WorkflowRuns");
        migrationBuilder.DropIndex(name: "IX_Issues_ProjectId_EpicNumber_Number", table: "Issues");
        migrationBuilder.DropIndex(name: "IX_EpicActiveIssues_ProjectId_EpicNumber_IssueNumber", table: "EpicActiveIssues");
        migrationBuilder.DropIndex(name: "IX_EpicIssues_ProjectId_EpicNumber_IssueNumber", table: "EpicIssues");
        migrationBuilder.DropIndex(name: "IX_Epics_ProjectId_Number", table: "Epics");

        migrationBuilder.CreateIndex(
            name: "IX_Epics_ProjectId_Number",
            table: "Epics",
            columns: new[] { "ProjectId", "Number" });

        migrationBuilder.AlterColumn<int>(
            name: "EpicNumber",
            table: "EpicActiveIssues",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER");

        migrationBuilder.AlterColumn<int>(
            name: "EpicNumber",
            table: "EpicIssues",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER");

        migrationBuilder.AlterColumn<int>(
            name: "Number",
            table: "Epics",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER");
    }
}
