using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentJobEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentJobEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeSortKey = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "strftime('%Y-%m-%dT%H:%M:%S', \"Time\") ||\nsubstr(\n    CASE\n        WHEN instr(substr(\"Time\", 20), '+') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '+') - 1)\n        WHEN instr(substr(\"Time\", 20), '-') > 0 THEN substr(\"Time\", 20, instr(substr(\"Time\", 20), '-') - 1)\n        ELSE ''\n    END || '.0000000',\n    1,\n    8\n) || 'Z'", stored: true),
                    DataStatus = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "LOWER(COALESCE(json_extract(\"Data\", '$.status'), json_extract(\"Data\", '$.Status')))", stored: true),
                    SpecVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "JSON", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "JSON", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentJobEvents", x => new { x.Source, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_DataStatus_Type_TimeSortKey_Source_Id",
                table: "AgentJobEvents",
                columns: new[] { "DataStatus", "Type", "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Source_EventId",
                table: "AgentJobEvents",
                columns: new[] { "Source", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_TimeSortKey_Source_Id",
                table: "AgentJobEvents",
                columns: new[] { "TimeSortKey", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Type_Source_Id",
                table: "AgentJobEvents",
                columns: new[] { "Type", "Source", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Type_Time",
                table: "AgentJobEvents",
                columns: new[] { "Type", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentJobEvents_Undelivered",
                table: "AgentJobEvents",
                columns: new[] { "Source", "Id" },
                filter: "\"DispatchedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentJobEvents");
        }
    }
}
