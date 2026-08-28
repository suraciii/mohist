using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddGitHubAppIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"LastErrorAt\" TEXT NULL;");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"LastErrorCode\" TEXT NULL;");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"LastErrorDetail\" TEXT NULL;");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"ReconnectRequired\" INTEGER NOT NULL DEFAULT 0;");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"RepositoryNodeId\" TEXT NULL;");
        migrationBuilder.Sql("""
            UPDATE "GitHubConnections"
            SET "Status" = 'disabled',
                "IdentityKind" = 'app',
                "ReconnectRequired" = 1,
                "NeedsAttention" = 1,
                "NeedsReprojection" = 1,
                "InstallationId" = NULL,
                "RepositoryNodeId" = NULL,
                "LastErrorCode" = 'github_app_reconnect_required',
                "LastErrorDetail" = 'Reconnect this connection through the GitHub App.',
                "LastErrorAt" = CURRENT_TIMESTAMP;
            """);
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubConnections_Owner_Repo\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubConnections_ProjectId_RepositoryName\";");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" RENAME TO \"__GitHubConnectionsBeforeAppIdentity\";");
        migrationBuilder.Sql("""
            CREATE TABLE "GitHubConnections" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_GitHubConnections" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Owner" TEXT NOT NULL,
                "Repo" TEXT NOT NULL,
                "RepositoryName" TEXT NOT NULL,
                "ApproversJson" JSON NOT NULL,
                "Status" TEXT NOT NULL,
                "InstallationId" TEXT NULL,
                "NeedsAttention" INTEGER NOT NULL,
                "NeedsReprojection" INTEGER NOT NULL,
                "LastErrorCode" TEXT NULL,
                "LastErrorDetail" TEXT NULL,
                "LastErrorAt" TEXT NULL,
                "ReconnectRequired" INTEGER NOT NULL,
                "RepositoryNodeId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "GitHubConnections" ("Id", "ProjectId", "Owner", "Repo", "RepositoryName", "ApproversJson", "Status", "InstallationId", "NeedsAttention", "NeedsReprojection", "LastErrorCode", "LastErrorDetail", "LastErrorAt", "ReconnectRequired", "RepositoryNodeId", "CreatedAt", "UpdatedAt")
            SELECT "Id", "ProjectId", "Owner", "Repo", "RepositoryName", "ApproversJson", "Status", "InstallationId", "NeedsAttention", "NeedsReprojection", "LastErrorCode", "LastErrorDetail", "LastErrorAt", "ReconnectRequired", "RepositoryNodeId", "CreatedAt", "UpdatedAt"
            FROM "__GitHubConnectionsBeforeAppIdentity";
            """);
        migrationBuilder.Sql("DROP TABLE \"__GitHubConnectionsBeforeAppIdentity\";");
        migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_GitHubConnections_Owner_Repo\" ON \"GitHubConnections\" (\"Owner\", \"Repo\");");
        migrationBuilder.Sql("CREATE INDEX \"IX_GitHubConnections_ProjectId_RepositoryName\" ON \"GitHubConnections\" (\"ProjectId\", \"RepositoryName\");");
        migrationBuilder.Sql("DELETE FROM \"StoredSecrets\" WHERE \"OwnerKind\" = 'github_connection' AND \"Kind\" = 'appToken';");
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_StoredSecrets_Owner";
            """);
        migrationBuilder.Sql("ALTER TABLE \"StoredSecrets\" RENAME TO \"__StoredSecretsBeforeGitHubAppIdentity\";");
        migrationBuilder.Sql("""
            CREATE TABLE "StoredSecrets" (
                "OwnerKind" TEXT NOT NULL,
                "OwnerScope" TEXT NOT NULL,
                "OwnerId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Blob" BLOB NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_StoredSecrets" PRIMARY KEY ("OwnerKind", "OwnerScope", "OwnerId", "Kind"),
                CONSTRAINT "CK_StoredSecrets_OwnerKind" CHECK ("OwnerKind" IN ('agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app', 'github_connection', 'server')),
                CONSTRAINT "CK_StoredSecrets_Kind" CHECK ("Kind" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken', 'publicApiCursorKey', 'githubAppCredential')),
                CONSTRAINT "CK_StoredSecrets_OwnerKindKind" CHECK (("OwnerKind" = 'agent_connection' AND "Kind" IN ('appToken', 'botToken')) OR ("OwnerKind" = 'webhook_subscription' AND "Kind" = 'webhookSecret') OR ("OwnerKind" = 'slack_workspace_enrollment' AND "Kind" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR ("OwnerKind" = 'managed_slack_agent_app' AND "Kind" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR ("OwnerKind" = 'github_connection' AND "Kind" = 'appToken') OR ("OwnerKind" = 'server' AND "Kind" IN ('publicApiCursorKey', 'githubAppCredential')))
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "StoredSecrets" ("OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            SELECT "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt"
            FROM "__StoredSecretsBeforeGitHubAppIdentity";
            """);
        migrationBuilder.Sql("DROP TABLE \"__StoredSecretsBeforeGitHubAppIdentity\";");
        migrationBuilder.Sql("CREATE INDEX \"IX_StoredSecrets_Owner\" ON \"StoredSecrets\" (\"OwnerKind\", \"OwnerScope\", \"OwnerId\");");
        migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_GitHubConnections_RepositoryNodeId\" ON \"GitHubConnections\" (\"RepositoryNodeId\") WHERE \"RepositoryNodeId\" IS NOT NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubConnections_RepositoryNodeId\";");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"IdentityKind\" TEXT NOT NULL DEFAULT 'app';");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" DROP COLUMN \"LastErrorAt\";");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" DROP COLUMN \"LastErrorCode\";");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" DROP COLUMN \"LastErrorDetail\";");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" DROP COLUMN \"ReconnectRequired\";");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" DROP COLUMN \"RepositoryNodeId\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_StoredSecrets_Owner\";");
        migrationBuilder.Sql("ALTER TABLE \"StoredSecrets\" RENAME TO \"__StoredSecretsBeforeGitHubAppIdentityDown\";");
        migrationBuilder.Sql("""
            CREATE TABLE "StoredSecrets" (
                "OwnerKind" TEXT NOT NULL,
                "OwnerScope" TEXT NOT NULL,
                "OwnerId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Blob" BLOB NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_StoredSecrets" PRIMARY KEY ("OwnerKind", "OwnerScope", "OwnerId", "Kind"),
                CONSTRAINT "CK_StoredSecrets_OwnerKind" CHECK ("OwnerKind" IN ('agent_connection', 'webhook_subscription', 'slack_workspace_enrollment', 'managed_slack_agent_app', 'github_connection', 'server')),
                CONSTRAINT "CK_StoredSecrets_Kind" CHECK ("Kind" IN ('appToken', 'botToken', 'webhookSecret', 'clientSecret', 'signingSecret', 'configurationAccessToken', 'configurationRefreshToken', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken', 'publicApiCursorKey')),
                CONSTRAINT "CK_StoredSecrets_OwnerKindKind" CHECK (("OwnerKind" = 'agent_connection' AND "Kind" IN ('appToken', 'botToken')) OR ("OwnerKind" = 'webhook_subscription' AND "Kind" = 'webhookSecret') OR ("OwnerKind" = 'slack_workspace_enrollment' AND "Kind" IN ('configurationAccessToken', 'configurationRefreshToken', 'appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR ("OwnerKind" = 'managed_slack_agent_app' AND "Kind" IN ('appToken', 'botToken', 'clientSecret', 'signingSecret', 'previousBotToken', 'previousAppToken', 'candidateBotToken', 'candidateAppToken')) OR ("OwnerKind" = 'github_connection' AND "Kind" = 'appToken') OR ("OwnerKind" = 'server' AND "Kind" = 'publicApiCursorKey'))
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "StoredSecrets" ("OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt")
            SELECT "OwnerKind", "OwnerScope", "OwnerId", "Kind", "Blob", "UpdatedAt"
            FROM "__StoredSecretsBeforeGitHubAppIdentityDown";
            """);
        migrationBuilder.Sql("DROP TABLE \"__StoredSecretsBeforeGitHubAppIdentityDown\";");
        migrationBuilder.Sql("CREATE INDEX \"IX_StoredSecrets_Owner\" ON \"StoredSecrets\" (\"OwnerKind\", \"OwnerScope\", \"OwnerId\");");
    }
}
