using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddWebhookSubscriptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            PRAGMA foreign_keys = 0;
            CREATE TABLE "__ConnectionSecrets" (
                "ProjectId" TEXT NOT NULL,
                "ConnectionId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Blob" BLOB NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_ConnectionSecrets" PRIMARY KEY ("ProjectId", "ConnectionId", "Kind"),
                CONSTRAINT "CK_ConnectionSecrets_Kind" CHECK ("Kind" IN ('appToken', 'botToken', 'webhookSecret'))
            );
            INSERT INTO "__ConnectionSecrets" ("ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
            SELECT "ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt" FROM "ConnectionSecrets";
            DROP TABLE "ConnectionSecrets";
            ALTER TABLE "__ConnectionSecrets" RENAME TO "ConnectionSecrets";
            CREATE INDEX "IX_ConnectionSecrets_ProjectId_ConnectionId" ON "ConnectionSecrets" ("ProjectId", "ConnectionId");
            PRAGMA foreign_keys = 1;
            """);

        migrationBuilder.CreateTable(
            name: "WebhookSubscriptions",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Match = table.Column<string>(type: "TEXT", nullable: false),
                TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "WebhookDeliveryFailures",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                SubscriptionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_WebhookDeliveryFailures", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "UX_WebhookSubscriptions_ProjectId_Name",
            table: "WebhookSubscriptions",
            columns: new[] { "ProjectId", "Name" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_WebhookSubscriptions_ProjectId_Status",
            table: "WebhookSubscriptions",
            columns: new[] { "ProjectId", "Status" });
        migrationBuilder.CreateIndex(
            name: "IX_WebhookSubscriptions_ProjectId",
            table: "WebhookSubscriptions",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryFailures_ProjectId_SubscriptionId",
            table: "WebhookDeliveryFailures",
            columns: new[] { "ProjectId", "SubscriptionId" });
        migrationBuilder.CreateIndex(
            name: "IX_WebhookDeliveryFailures_ProjectId",
            table: "WebhookDeliveryFailures",
            column: "ProjectId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "WebhookDeliveryFailures");
        migrationBuilder.DropTable(name: "WebhookSubscriptions");

        migrationBuilder.Sql("""
            PRAGMA foreign_keys = 0;
            CREATE TABLE "__ConnectionSecrets" (
                "ProjectId" TEXT NOT NULL,
                "ConnectionId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Blob" BLOB NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_ConnectionSecrets" PRIMARY KEY ("ProjectId", "ConnectionId", "Kind"),
                CONSTRAINT "CK_ConnectionSecrets_Kind" CHECK ("Kind" IN ('appToken', 'botToken'))
            );
            INSERT INTO "__ConnectionSecrets" ("ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt")
            SELECT "ProjectId", "ConnectionId", "Kind", "Blob", "UpdatedAt" FROM "ConnectionSecrets"
            WHERE "Kind" IN ('appToken', 'botToken');
            DROP TABLE "ConnectionSecrets";
            ALTER TABLE "__ConnectionSecrets" RENAME TO "ConnectionSecrets";
            CREATE INDEX "IX_ConnectionSecrets_ProjectId_ConnectionId" ON "ConnectionSecrets" ("ProjectId", "ConnectionId");
            PRAGMA foreign_keys = 1;
            """);
    }
}
