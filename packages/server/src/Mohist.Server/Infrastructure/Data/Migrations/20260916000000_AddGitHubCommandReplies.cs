using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubCommandReplies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        RebuildStoredSecrets(
            migrationBuilder,
            ownerKinds: "'agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app', 'github_connection', 'server'",
            ownerKindPairs: "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR " +
                "(\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR " +
                "(\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'github_connection' AND \"Kind\" = 'appToken') OR " +
                "(\"OwnerKind\" = 'server' AND \"Kind\" = 'publicApiCursorKey')");

        migrationBuilder.AddColumn<bool>(
            name: "CommandRequested",
            table: "GitHubIssueLinks",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "GitHubCommandReplies",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                RepositoryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                GithubIssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                GithubCommentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Marker = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Body = table.Column<string>(type: "TEXT", nullable: false),
                PostedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GitHubCommandReplies", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "UX_GitHubCommandReplies_Connection_Issue_Comment",
            table: "GitHubCommandReplies",
            columns: new[] { "ConnectionId", "GithubIssueNumber", "GithubCommentId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GitHubCommandReplies");
        migrationBuilder.DropColumn(
            name: "CommandRequested",
            table: "GitHubIssueLinks");
        migrationBuilder.Sql(
            "DELETE FROM \"StoredSecrets\" WHERE \"OwnerKind\" = 'github_connection';");
        RebuildStoredSecrets(
            migrationBuilder,
            ownerKinds: "'agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app', 'server'",
            ownerKindPairs: "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR " +
                "(\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR " +
                "(\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'server' AND \"Kind\" = 'publicApiCursorKey')");
    }

    private static void RebuildStoredSecrets(
        MigrationBuilder migrationBuilder,
        string ownerKinds,
        string ownerKindPairs)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_StoredSecrets_Owner\";");
        migrationBuilder.Sql("ALTER TABLE \"StoredSecrets\" RENAME TO \"__StoredSecretsBeforeGitHubPat\";");
        migrationBuilder.Sql($"""
            CREATE TABLE "StoredSecrets" (
                "OwnerKind" TEXT NOT NULL,
                "OwnerScope" TEXT NOT NULL,
                "OwnerId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Blob" BLOB NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_StoredSecrets" PRIMARY KEY ("OwnerKind", "OwnerScope", "OwnerId", "Kind"),
                CONSTRAINT "CK_StoredSecrets_OwnerKind" CHECK ("OwnerKind" IN ({ownerKinds})),
                CONSTRAINT "CK_StoredSecrets_Kind" CHECK ("Kind" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken', 'publicApiCursorKey')),
                CONSTRAINT "CK_StoredSecrets_OwnerKindKind" CHECK ({ownerKindPairs})
            );
            INSERT INTO "StoredSecrets" ("OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            SELECT "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt"
            FROM "__StoredSecretsBeforeGitHubPat";
            DROP TABLE "__StoredSecretsBeforeGitHubPat";
            CREATE INDEX "IX_StoredSecrets_Owner"
                ON "StoredSecrets" ("OwnerKind", "OwnerScope", "OwnerId");
            """);
    }
}
