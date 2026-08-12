using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260904000000_AddWorkflowArtifactSourceUploadId")]
public partial class AddWorkflowArtifactSourceUploadId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SourceUploadId",
            table: "WorkflowArtifacts",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "UX_WorkflowArtifacts_SourceUploadId",
            table: "WorkflowArtifacts",
            column: "SourceUploadId",
            unique: true,
            filter: "\"SourceUploadId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_WorkflowArtifacts_SourceUploadId",
            table: "WorkflowArtifacts");

        migrationBuilder.Sql(
            "ALTER TABLE \"WorkflowArtifacts\" DROP COLUMN \"SourceUploadId\";");
    }
}
