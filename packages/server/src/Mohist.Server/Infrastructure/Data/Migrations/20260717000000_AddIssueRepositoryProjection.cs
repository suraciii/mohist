using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// issue-417 T-002 / Design D2/D3: persistence projection for the issue
/// target-repository lifecycle. See the comment on
/// <see cref="AddIssueRepositoryProjection"/> for the rationale and
/// SQLite-specific steps.
/// </summary>
[DbContext(typeof(MohistDbContext))]
[Migration("20260717000000_AddIssueRepositoryProjection")]
public partial class AddIssueRepositoryProjection : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        MohistDbContextModelSnapshot.BuildModelCore(modelBuilder);

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
