using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueIsArchivedComputedColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status'))",
                stored: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Issues",
                type: "INTEGER",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.archivedAt') IS NOT NULL",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_Status",
                table: "Issues",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Issues_Status",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Issues");
        }
    }
}
