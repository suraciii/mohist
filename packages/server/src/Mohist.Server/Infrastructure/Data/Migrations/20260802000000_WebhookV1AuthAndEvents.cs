using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class WebhookV1AuthAndEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Webhook v1: endpoint authentication + event selection on subscriptions.
        migrationBuilder.AddColumn<string>(
            name: "AuthType",
            table: "WebhookSubscriptions",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "none");

        migrationBuilder.AddColumn<string>(
            name: "EventSelectionMode",
            table: "WebhookSubscriptions",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "all");

        migrationBuilder.AddColumn<string>(
            name: "EventTypes",
            table: "WebhookSubscriptions",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        // Richer delivery diagnostics: HTTP status and duration for failed deliveries.
        migrationBuilder.AddColumn<int>(
            name: "ResponseStatus",
            table: "WebhookDeliveryFailures",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "DurationMs",
            table: "WebhookDeliveryFailures",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ResponseStatus", table: "WebhookDeliveryFailures");
        migrationBuilder.DropColumn(name: "DurationMs", table: "WebhookDeliveryFailures");
        migrationBuilder.DropColumn(name: "AuthType", table: "WebhookSubscriptions");
        migrationBuilder.DropColumn(name: "EventSelectionMode", table: "WebhookSubscriptions");
        migrationBuilder.DropColumn(name: "EventTypes", table: "WebhookSubscriptions");
    }
}
