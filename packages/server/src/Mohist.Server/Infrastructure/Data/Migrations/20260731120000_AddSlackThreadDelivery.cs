using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackThreadDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "DmConversationId",
            table: "SlackProviderInboxRows",
            newName: "ConversationId");

        migrationBuilder.RenameColumn(
            name: "DmConversationId",
            table: "SlackOutboxRows",
            newName: "ConversationId");

        migrationBuilder.AddColumn<string>(
            name: "ThreadTs",
            table: "SlackOutboxRows",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LabelSlackThreadTs",
            table: "AgentSessions",
            type: "TEXT",
            nullable: true,
            computedColumnSql: "json_extract(\"State\", '$.metadata.labels.\"mohist.io/slack-thread-ts\"')",
            stored: true);

        migrationBuilder.CreateIndex(
            name: "IX_AgentSessions_LabelSlackThreadTs",
            table: "AgentSessions",
            column: "LabelSlackThreadTs");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentSessions_LabelSlackThreadTs",
            table: "AgentSessions");

        migrationBuilder.DropColumn(
            name: "LabelSlackThreadTs",
            table: "AgentSessions");

        migrationBuilder.DropColumn(
            name: "ThreadTs",
            table: "SlackOutboxRows");

        migrationBuilder.RenameColumn(
            name: "ConversationId",
            table: "SlackOutboxRows",
            newName: "DmConversationId");

        migrationBuilder.RenameColumn(
            name: "ConversationId",
            table: "SlackProviderInboxRows",
            newName: "DmConversationId");
    }
}
