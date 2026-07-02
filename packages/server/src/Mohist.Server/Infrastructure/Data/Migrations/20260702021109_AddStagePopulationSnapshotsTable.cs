using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStagePopulationSnapshotsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StagePopulationSnapshots",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Backlog = table.Column<int>(type: "INTEGER", nullable: false),
                    Plan = table.Column<int>(type: "INTEGER", nullable: false),
                    Build = table.Column<int>(type: "INTEGER", nullable: false),
                    Check = table.Column<int>(type: "INTEGER", nullable: false),
                    Integrate = table.Column<int>(type: "INTEGER", nullable: false),
                    Done = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagePopulationSnapshots", x => new { x.ProjectId, x.Day });
                });

            migrationBuilder.CreateIndex(
                name: "UQ_StagePopulationSnapshots_ProjectId_Day",
                table: "StagePopulationSnapshots",
                columns: new[] { "ProjectId", "Day" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StagePopulationSnapshots");
        }
    }
}
