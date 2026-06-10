using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixComputedColumnsCamelCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "MetadataProjectId",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.metadata.annotations.projectId')",
                stored: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.Metadata.Annotations.projectId')",
                oldStored: true);

            migrationBuilder.AlterColumn<string>(
                name: "WorkflowRunId",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.workflowRunId')",
                stored: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.WorkflowRunId')",
                oldStored: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProjectId",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.projectId')",
                stored: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.ProjectId')",
                oldStored: true);

            migrationBuilder.AlterColumn<int>(
                name: "Number",
                table: "Issues",
                type: "INTEGER",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.number')",
                stored: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.Number')",
                oldStored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "MetadataProjectId",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.Metadata.Annotations.projectId')",
                stored: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.metadata.annotations.projectId')",
                oldStored: true);

            migrationBuilder.AlterColumn<string>(
                name: "WorkflowRunId",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.WorkflowRunId')",
                stored: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.workflowRunId')",
                oldStored: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProjectId",
                table: "Issues",
                type: "TEXT",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.ProjectId')",
                stored: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.projectId')",
                oldStored: true);

            migrationBuilder.AlterColumn<int>(
                name: "Number",
                table: "Issues",
                type: "INTEGER",
                nullable: true,
                computedColumnSql: "json_extract(State, '$.Number')",
                stored: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true,
                oldComputedColumnSql: "json_extract(State, '$.number')",
                oldStored: true);
        }
    }
}
