using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260807000000_AddSlackAgentAppInstallValidation")]
public partial class AddSlackAgentAppInstallValidation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(
            name: "SlackChildAppBindingObligations",
            newName: "SlackAgentAppBindingObligations");
        migrationBuilder.RenameColumn(
            name: "ChildAppId",
            table: "SlackAgentAppBindingObligations",
            newName: "AgentAppId");
        migrationBuilder.DropIndex(
            name: "IX_SlackChildAppBindingObligations_AgentConnectionId",
            table: "SlackAgentAppBindingObligations");
        migrationBuilder.DropIndex(
            name: "IX_SlackChildAppBindingObligations_Status_UpdatedAt",
            table: "SlackAgentAppBindingObligations");
        migrationBuilder.DropIndex(
            name: "UX_SlackChildAppBindingObligations_ChildAppId",
            table: "SlackAgentAppBindingObligations");
        migrationBuilder.CreateIndex(
            name: "IX_SlackAgentAppBindingObligations_AgentConnectionId",
            table: "SlackAgentAppBindingObligations",
            column: "AgentConnectionId");
        migrationBuilder.CreateIndex(
            name: "IX_SlackAgentAppBindingObligations_Status_UpdatedAt",
            table: "SlackAgentAppBindingObligations",
            columns: new[] { "Status", "UpdatedAt" });
        migrationBuilder.CreateIndex(
            name: "UX_SlackAgentAppBindingObligations_AgentAppId",
            table: "SlackAgentAppBindingObligations",
            column: "AgentAppId",
            unique: true);

        migrationBuilder.RenameColumn(
            name: "ChildAppId",
            table: "SlackOAuthAttempts",
            newName: "AgentAppId");
        migrationBuilder.DropIndex(
            name: "IX_SlackOAuthAttempts_ChildAppId_Status_UpdatedAt",
            table: "SlackOAuthAttempts");
        migrationBuilder.CreateIndex(
            name: "IX_SlackOAuthAttempts_AgentAppId_Status_UpdatedAt",
            table: "SlackOAuthAttempts",
            columns: new[] { "AgentAppId", "Status", "UpdatedAt" });

        migrationBuilder.RenameColumn(
            name: "ChildAppId",
            table: "SlackOAuthStates",
            newName: "AgentAppId");
        migrationBuilder.DropIndex(
            name: "IX_SlackOAuthStates_ChildAppId_ConsumedAt_ExpiresAt",
            table: "SlackOAuthStates");
        migrationBuilder.CreateIndex(
            name: "IX_SlackOAuthStates_AgentAppId_ConsumedAt_ExpiresAt",
            table: "SlackOAuthStates",
            columns: new[] { "AgentAppId", "ConsumedAt", "ExpiresAt" });

        migrationBuilder.AddColumn<string>(
            name: "InstallUrl",
            table: "ManagedSlackAgentApps",
            type: "TEXT",
            maxLength: 1024,
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "RuntimeCredentialValidationState",
            table: "ManagedSlackAgentApps",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "not_provided");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql("ALTER TABLE \"ManagedSlackAgentApps\" DROP COLUMN \"RuntimeCredentialValidationState\";");
            migrationBuilder.Sql("ALTER TABLE \"ManagedSlackAgentApps\" DROP COLUMN \"InstallUrl\";");
        }
        else
        {
            migrationBuilder.DropColumn(
                name: "RuntimeCredentialValidationState",
                table: "ManagedSlackAgentApps");
            migrationBuilder.DropColumn(
                name: "InstallUrl",
                table: "ManagedSlackAgentApps");
        }

        migrationBuilder.DropIndex(
            name: "IX_SlackOAuthStates_AgentAppId_ConsumedAt_ExpiresAt",
            table: "SlackOAuthStates");
        migrationBuilder.RenameColumn(
            name: "AgentAppId",
            table: "SlackOAuthStates",
            newName: "ChildAppId");
        migrationBuilder.CreateIndex(
            name: "IX_SlackOAuthStates_ChildAppId_ConsumedAt_ExpiresAt",
            table: "SlackOAuthStates",
            columns: new[] { "ChildAppId", "ConsumedAt", "ExpiresAt" });

        migrationBuilder.DropIndex(
            name: "IX_SlackOAuthAttempts_AgentAppId_Status_UpdatedAt",
            table: "SlackOAuthAttempts");
        migrationBuilder.RenameColumn(
            name: "AgentAppId",
            table: "SlackOAuthAttempts",
            newName: "ChildAppId");
        migrationBuilder.CreateIndex(
            name: "IX_SlackOAuthAttempts_ChildAppId_Status_UpdatedAt",
            table: "SlackOAuthAttempts",
            columns: new[] { "ChildAppId", "Status", "UpdatedAt" });

        migrationBuilder.DropIndex(
            name: "UX_SlackAgentAppBindingObligations_AgentAppId",
            table: "SlackAgentAppBindingObligations");
        migrationBuilder.DropIndex(
            name: "IX_SlackAgentAppBindingObligations_Status_UpdatedAt",
            table: "SlackAgentAppBindingObligations");
        migrationBuilder.DropIndex(
            name: "IX_SlackAgentAppBindingObligations_AgentConnectionId",
            table: "SlackAgentAppBindingObligations");
        migrationBuilder.RenameColumn(
            name: "AgentAppId",
            table: "SlackAgentAppBindingObligations",
            newName: "ChildAppId");
        migrationBuilder.RenameTable(
            name: "SlackAgentAppBindingObligations",
            newName: "SlackChildAppBindingObligations");
        migrationBuilder.CreateIndex(
            name: "UX_SlackChildAppBindingObligations_ChildAppId",
            table: "SlackChildAppBindingObligations",
            column: "ChildAppId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_SlackChildAppBindingObligations_Status_UpdatedAt",
            table: "SlackChildAppBindingObligations",
            columns: new[] { "Status", "UpdatedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_SlackChildAppBindingObligations_AgentConnectionId",
            table: "SlackChildAppBindingObligations",
            column: "AgentConnectionId");
    }
}
