using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackAmbiguousPrompts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SlackAmbiguousPrompts",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                MessageTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ThreadTs = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                WinningConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                MentionedConnectionIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                PromptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SlackAmbiguousPrompts", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "UX_SlackAmbiguousPrompts_WorkspaceTeamId_ConversationId_MessageTs",
            table: "SlackAmbiguousPrompts",
            columns: new[] { "WorkspaceTeamId", "ConversationId", "MessageTs" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SlackAmbiguousPrompts_ProjectId_UpdatedAt",
            table: "SlackAmbiguousPrompts",
            columns: new[] { "ProjectId", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SlackAmbiguousPrompts");
    }
}