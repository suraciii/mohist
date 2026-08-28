using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class RemoveGitHubFeedOptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_GitHubConnections_ProjectId_RepositoryName\";");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" RENAME TO \"__GitHubConnectionsBeforeFeedOptions\";");
        migrationBuilder.Sql("""
            CREATE TABLE "GitHubConnections" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_GitHubConnections" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Owner" TEXT NOT NULL,
                "Repo" TEXT NOT NULL,
                "RepositoryName" TEXT NOT NULL,
                "ApproversJson" JSON NOT NULL,
                "Status" TEXT NOT NULL,
                "IdentityKind" TEXT NOT NULL,
                "InstallationId" TEXT NULL,
                "NeedsAttention" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        migrationBuilder.Sql("""
            INSERT INTO "GitHubConnections" ("Id", "ProjectId", "Owner", "Repo", "RepositoryName", "ApproversJson", "Status", "IdentityKind", "InstallationId", "NeedsAttention", "CreatedAt", "UpdatedAt")
            SELECT "Id", "ProjectId", "Owner", "Repo", "RepositoryName", "ApproversJson", "Status", "IdentityKind", "InstallationId", "NeedsAttention", "CreatedAt", "UpdatedAt"
            FROM "__GitHubConnectionsBeforeFeedOptions";
            """);
        migrationBuilder.Sql("DROP TABLE \"__GitHubConnectionsBeforeFeedOptions\";");
        migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_GitHubConnections_Owner_Repo\" ON \"GitHubConnections\" (\"Owner\", \"Repo\");");
        migrationBuilder.Sql("CREATE INDEX \"IX_GitHubConnections_ProjectId_RepositoryName\" ON \"GitHubConnections\" (\"ProjectId\", \"RepositoryName\");");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"FeedMode\" TEXT NOT NULL DEFAULT 'start';");
        migrationBuilder.Sql("ALTER TABLE \"GitHubConnections\" ADD COLUMN \"IntakeLabel\" TEXT NOT NULL DEFAULT 'mohist';");
    }
}
