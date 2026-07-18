using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddIssueRepositoryProjection : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RepositoryName",
            table: "Issues",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "RepositoryName",
            table: "Issues",
            type: "TEXT",
            nullable: true,
            computedColumnSql: "COALESCE(json_extract(\"State\", '$.repositoryRef'), json_extract(\"State\", '$.RepositoryRef'))",
            stored: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Issues_ProjectId_RepositoryName_Status",
            table: "Issues",
            columns: new[] { "ProjectId", "RepositoryName", "Status" });

        migrationBuilder.AddColumn<long>(
            name: "RepositoryRevision",
            table: "Projects",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "LastRepositoryCommandJson",
            table: "Projects",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LastRepositoryCommandJson",
            table: "Projects");

        migrationBuilder.DropColumn(
            name: "RepositoryRevision",
            table: "Projects");

        migrationBuilder.DropIndex(
            name: "IX_Issues_ProjectId_RepositoryName_Status",
            table: "Issues");

        migrationBuilder.DropColumn(
            name: "RepositoryName",
            table: "Issues");
    }
}
