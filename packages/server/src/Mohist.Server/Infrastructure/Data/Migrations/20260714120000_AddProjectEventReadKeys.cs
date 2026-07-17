using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260714120000_AddProjectEventReadKeys")]
public partial class AddProjectEventReadKeys : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        MohistDbContextModelSnapshot.BuildModelCore(modelBuilder);
        modelBuilder.SharedTypeEntity<Dictionary<string, object>>(typeof(IssueEventRow).FullName!)
            .Ignore(nameof(IssueEventRow.TimelineSource));
        modelBuilder.SharedTypeEntity<Dictionary<string, object>>(typeof(EpicEventRow).FullName!)
            .Ignore(nameof(EpicEventRow.TimelineSource));
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("TimeSortKey", "IssueEvents", "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("TimeSortKey", "WorkflowRunEvents", "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("TimeSortKey", "AgentSessionEvents", "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("DataStatus", "AgentSessionEvents", "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("PayloadStatus", "AgentSessionTranscriptParts", "TEXT", nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "TimeSortKey",
            table: "IssueEvents",
            type: "TEXT",
            nullable: true,
            computedColumnSql: EventReadKeys.TimeSortKeySql,
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "TimeSortKey",
            table: "WorkflowRunEvents",
            type: "TEXT",
            nullable: true,
            computedColumnSql: EventReadKeys.TimeSortKeySql,
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "TimeSortKey",
            table: "AgentSessionEvents",
            type: "TEXT",
            nullable: true,
            computedColumnSql: EventReadKeys.TimeSortKeySql,
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "DataStatus",
            table: "AgentSessionEvents",
            type: "TEXT",
            nullable: true,
            computedColumnSql: EventReadKeys.DataStatusSql,
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);
        migrationBuilder.AlterColumn<string>(
            name: "PayloadStatus",
            table: "AgentSessionTranscriptParts",
            type: "TEXT",
            nullable: true,
            computedColumnSql: EventReadKeys.PayloadStatusSql,
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_IssueEvents_TimeSortKey_Source_Id",
            table: "IssueEvents",
            columns: new[] { "TimeSortKey", "Source", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_WorkflowRunEvents_TimeSortKey_Source_Id",
            table: "WorkflowRunEvents",
            columns: new[] { "TimeSortKey", "Source", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_AgentSessionEvents_TimeSortKey_Source_Id",
            table: "AgentSessionEvents",
            columns: new[] { "TimeSortKey", "Source", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_AgentSessionEvents_DataStatus_Type_TimeSortKey_Source_Id",
            table: "AgentSessionEvents",
            columns: new[] { "DataStatus", "Type", "TimeSortKey", "Source", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_AgentSessionTranscriptParts_Type_PayloadStatus_LastSeenAt_Id",
            table: "AgentSessionTranscriptParts",
            columns: new[] { "Type", "PayloadStatus", "LastSeenAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_IssueEvents_TimeSortKey_Source_Id", "IssueEvents");
        migrationBuilder.DropIndex("IX_WorkflowRunEvents_TimeSortKey_Source_Id", "WorkflowRunEvents");
        migrationBuilder.DropIndex("IX_AgentSessionEvents_TimeSortKey_Source_Id", "AgentSessionEvents");
        migrationBuilder.DropIndex("IX_AgentSessionEvents_DataStatus_Type_TimeSortKey_Source_Id", "AgentSessionEvents");
        migrationBuilder.DropIndex("IX_AgentSessionTranscriptParts_Type_PayloadStatus_LastSeenAt_Id", "AgentSessionTranscriptParts");

        migrationBuilder.DropColumn("TimeSortKey", "IssueEvents");
        migrationBuilder.DropColumn("TimeSortKey", "WorkflowRunEvents");
        migrationBuilder.DropColumn("TimeSortKey", "AgentSessionEvents");
        migrationBuilder.DropColumn("DataStatus", "AgentSessionEvents");
        migrationBuilder.DropColumn("PayloadStatus", "AgentSessionTranscriptParts");
    }
}
