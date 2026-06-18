using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.projectId'), json_extract(State, '$.ProjectId'))", stored: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.name'), json_extract(State, '$.Name'))", stored: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status'))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ProjectId_Name",
                table: "Agents",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ProjectId_Status",
                table: "Agents",
                columns: new[] { "ProjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
