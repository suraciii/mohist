using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubCommentOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GitHubIssueCommentOperations",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                LinkId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CommentKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_GitHubIssueCommentOperations", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_GitHubIssueCommentOperations_LinkId",
            table: "GitHubIssueCommentOperations",
            column: "LinkId");

        migrationBuilder.CreateIndex(
            name: "IX_GitHubIssueCommentOperations_LinkId_CommentKey",
            table: "GitHubIssueCommentOperations",
            columns: new[] { "LinkId", "CommentKey" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "GitHubIssueCommentOperations");
    }
}
