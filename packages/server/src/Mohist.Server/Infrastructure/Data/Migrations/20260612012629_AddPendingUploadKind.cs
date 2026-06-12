using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingUploadKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FileCount",
                table: "WorkflowArtifactPendingUploads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "WorkflowArtifactPendingUploads",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "file");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileCount",
                table: "WorkflowArtifactPendingUploads");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "WorkflowArtifactPendingUploads");
        }
    }
}
