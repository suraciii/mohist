using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Extends the encrypted server-secret store with the deployment-wide
/// HMAC key used by public Session event cursors. Existing secret rows are
/// copied through a rebuilt SQLite table so the check constraints continue
/// to protect owner and kind combinations.
/// </summary>
public partial class AddPublicApiCursorSecret : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        RebuildStoredSecrets(
            migrationBuilder,
            ownerKinds: "'agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app', 'server'",
            kinds: "'appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken', 'publicApiCursorKey'",
            ownerKindPairs: "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR " +
                "(\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR " +
                "(\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'server' AND \"Kind\" = 'publicApiCursorKey')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "DELETE FROM \"StoredSecrets\" WHERE \"OwnerKind\" = 'server' AND \"Kind\" = 'publicApiCursorKey';");
        RebuildStoredSecrets(
            migrationBuilder,
            ownerKinds: "'agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app'",
            kinds: "'appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken'",
            ownerKindPairs: "(\"OwnerKind\" = 'agent_connection' AND \"Kind\" IN ('appToken', 'botToken')) OR " +
                "(\"OwnerKind\" = 'webhook_subscription' AND \"Kind\" = 'webhookSecret') OR " +
                "(\"OwnerKind\" = 'slack_workspace_enrollment' AND \"Kind\" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR " +
                "(\"OwnerKind\" = 'managed_slack_agent_app' AND \"Kind\" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken'))");
    }

    private static void RebuildStoredSecrets(
        MigrationBuilder migrationBuilder,
        string ownerKinds,
        string kinds,
        string ownerKindPairs)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_StoredSecrets_Owner\";");
        migrationBuilder.Sql("ALTER TABLE \"StoredSecrets\" RENAME TO \"__StoredSecretsBeforePublicApiCursorSecret\";");
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
                CONSTRAINT "CK_StoredSecrets_Kind" CHECK ("Kind" IN ({kinds})),
                CONSTRAINT "CK_StoredSecrets_OwnerKindKind" CHECK ({ownerKindPairs})
            );
            INSERT INTO "StoredSecrets" ("OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            SELECT "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt"
            FROM "__StoredSecretsBeforePublicApiCursorSecret";
            DROP TABLE "__StoredSecretsBeforePublicApiCursorSecret";
            CREATE INDEX "IX_StoredSecrets_Owner"
                ON "StoredSecrets" ("OwnerKind", "OwnerScope", "OwnerId");
            """);
    }
}
