using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260805120000_AddAgentSubagentTreeContracts")]
public partial class AddAgentSubagentTreeContracts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PinnedRunnerId",
            table: "AgentJobs",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LaunchVisibility",
            table: "AgentJobs",
            type: "TEXT",
            nullable: false,
            defaultValue: "visible");

        migrationBuilder.AddColumn<string>(
            name: "ChildLaunchJobId",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LaunchVisibility",
            table: "AgentSessions",
            type: "TEXT",
            nullable: false,
            defaultValue: "visible");

        migrationBuilder.AddColumn<string>(
            name: "ParentAgentId",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ParentLinkAttachedAt",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ParentLinkAttachedRevision",
            table: "AgentSessions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ParentLinkDetachedAt",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ParentLinkDetachedRevision",
            table: "AgentSessions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ParentLinkEdgeId",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ParentLinkState",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ParentSessionId",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_AgentJobs_LaunchVisibility_Status_ReadySince",
            table: "AgentJobs",
            columns: new[] { "LaunchVisibility", "Status", "ReadySince" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentJobs_PinnedRunner_Status_ReadySince",
            table: "AgentJobs",
            columns: new[] { "PinnedRunnerId", "Status", "ReadySince" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_TreeParent_AttachedRevision_Edge",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "ParentSessionId", "ParentLinkState", "ParentLinkAttachedRevision", "ParentLinkEdgeId" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_TreeVisibleParent_AttachedRevision_Edge",
            table: "AgentSessions",
            columns: new[] { "LabelProjectId", "LaunchVisibility", "ParentSessionId", "ParentLinkAttachedRevision", "ParentLinkEdgeId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentJobs_LaunchVisibility_Status_ReadySince",
            table: "AgentJobs");
        migrationBuilder.DropIndex(
            name: "IX_AgentJobs_PinnedRunner_Status_ReadySince",
            table: "AgentJobs");
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_TreeParent_AttachedRevision_Edge",
            table: "AgentSessions");
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_TreeVisibleParent_AttachedRevision_Edge",
            table: "AgentSessions");

        migrationBuilder.DropColumn(name: "PinnedRunnerId", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "LaunchVisibility", table: "AgentJobs");
        migrationBuilder.DropColumn(name: "ChildLaunchJobId", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "LaunchVisibility", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentAgentId", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentLinkAttachedAt", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentLinkAttachedRevision", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentLinkDetachedAt", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentLinkDetachedRevision", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentLinkEdgeId", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentLinkState", table: "AgentSessions");
        migrationBuilder.DropColumn(name: "ParentSessionId", table: "AgentSessions");
    }
}
