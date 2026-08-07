using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260819000000_AddWorkspaceEvents")]
public partial class AddWorkspaceEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WorkspaceEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                TimelineSource = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                TimeSortKey = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "strftime('%Y-%m-%dT%H:%M:%S', \"Time\") ||\nsubstr(\n    CASE\n        WHEN instr(substr(\"Time\", 20), '+') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '+') - 1)\n        WHEN instr(substr(\"Time\", 20), '-') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '-') - 1)\n        ELSE ''\n    END || '.0000000',\n    1,\n    8\n) || 'Z'", stored: true),
                SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Data = table.Column<string>(type: "JSON", nullable: false),
                ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkspaceEvents", x => new { x.Source, x.Id });
            });

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceEvents_Source_Id_DispatchedAt",
            table: "WorkspaceEvents",
            columns: new[] { "Source", "Id", "DispatchedAt" },
            filter: "\"DispatchedAt\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceEvents_TimelineSource_Time_Source_Id",
            table: "WorkspaceEvents",
            columns: new[] { "TimelineSource", "Time", "Source", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceEvents_TimeSortKey_Source_Id",
            table: "WorkspaceEvents",
            columns: new[] { "TimeSortKey", "Source", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceEvents_Type_Source_Id",
            table: "WorkspaceEvents",
            columns: new[] { "Type", "Source", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WorkspaceEvents");
    }
}
