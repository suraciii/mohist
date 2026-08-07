using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260818000000_AddWorkspace")]
public partial class AddWorkspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Workspaces",
            columns: table => new
            {
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                OriginKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                OriginPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                RepositoriesJson = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                HomeRunnerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                HomePath = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ArchivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Workspaces", x => new { x.ProjectId, x.Name });
            });

        migrationBuilder.CreateIndex(
            name: "IX_Workspaces_ProjectId_OriginKind_OriginPayloadJson",
            table: "Workspaces",
            columns: new[] { "ProjectId", "OriginKind", "OriginPayloadJson" },
            unique: true,
            filter: "\"Status\" = 'active'");

        migrationBuilder.AddColumn<string>(
            name: "LabelWorkspaceName",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "LabelWorkspaceName",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true,
            computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/workspace-name\"')",
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelWorkspaceName",
            table: "AgentSessions",
            column: "LabelWorkspaceName");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelWorkspaceName",
            table: "AgentSessions");

        migrationBuilder.Sql(
            "ALTER TABLE \"AgentSessions\" DROP COLUMN \"LabelWorkspaceName\";");

        migrationBuilder.DropTable(
            name: "Workspaces");
    }
}
