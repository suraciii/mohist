using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpicListDerivedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDraft",
                table: "Issues",
                type: "INTEGER",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.isDraft'), json_extract(State, '$.IsDraft'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "PrerequisiteNumbersJson",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.prerequisiteNumbers'), json_extract(State, '$.PrerequisiteNumbers'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.priority'), json_extract(State, '$.Priority'))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "COALESCE(json_extract(State, '$.title'), json_extract(State, '$.Title'))",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDraft",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "PrerequisiteNumbersJson",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Issues");
        }
    }
}
