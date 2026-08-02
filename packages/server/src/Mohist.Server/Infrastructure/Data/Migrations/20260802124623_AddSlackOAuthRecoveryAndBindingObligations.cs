using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSlackOAuthRecoveryAndBindingObligations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorizationAttemptId",
                table: "SlackOAuthStates",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SlackChildAppBindingObligations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChildAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FailureClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackChildAppBindingObligations", x => x.Id);
                    table.CheckConstraint("CK_SlackChildAppBindingObligations_Status", "\"Status\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')");
                    table.ForeignKey(
                        name: "FK_SlackChildAppBindingObligations_AgentConnections_AgentConnectionId",
                        column: x => x.AgentConnectionId,
                        principalTable: "AgentConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlackChildAppBindingObligations_ManagedSlackChildApps_ChildAppId",
                        column: x => x.ChildAppId,
                        principalTable: "ManagedSlackChildApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlackOAuthAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChildAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BotTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    FailureClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SecretStoredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackOAuthAttempts", x => x.Id);
                    table.CheckConstraint("CK_SlackOAuthAttempts_Status", "\"Status\" IN ('issued', 'consumed', 'secret_stored', 'applied', 'expired', 'recovery_required')");
                    table.ForeignKey(
                        name: "FK_SlackOAuthAttempts_ManagedSlackChildApps_ChildAppId",
                        column: x => x.ChildAppId,
                        principalTable: "ManagedSlackChildApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_Lifecycle",
                table: "SlackWorkspaceEnrollments",
                sql: "\"Lifecycle\" IN ('active', 'disabled', 'removed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_ManagerCapability",
                table: "SlackWorkspaceEnrollments",
                sql: "\"ManagerCapability\" IN ('unknown', 'available', 'unauthorized', 'capacity_limited')");

            migrationBuilder.CreateIndex(
                name: "IX_SlackOAuthStates_AuthorizationAttemptId",
                table: "SlackOAuthStates",
                column: "AuthorizationAttemptId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SlackOAuthStates_Outcome",
                table: "SlackOAuthStates",
                sql: "\"Outcome\" IS NULL OR \"Outcome\" IN ('accepted', 'expired')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagedSlackChildApps_AppliedManifestPair",
                table: "ManagedSlackChildApps",
                sql: "(\"AppliedManifestVersion\" IS NULL AND \"AppliedManifestHash\" IS NULL) OR (\"AppliedManifestVersion\" IS NOT NULL AND \"AppliedManifestHash\" IS NOT NULL AND \"AppliedManifestVersion\" > 0 AND \"AppliedManifestHash\" <> '')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagedSlackChildApps_AppLifecycle",
                table: "ManagedSlackChildApps",
                sql: "\"AppLifecycle\" IN ('not_created', 'creating', 'create_unknown', 'created', 'deleting', 'delete_unknown', 'deleted')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagedSlackChildApps_Authorization",
                table: "ManagedSlackChildApps",
                sql: "\"Authorization\" IN ('not_started', 'awaiting_user', 'pending_admin', 'authorized', 'expired_or_cancelled', 'revoked')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagedSlackChildApps_BindingState",
                table: "ManagedSlackChildApps",
                sql: "\"BindingState\" IN ('pending', 'in_progress', 'bound', 'connection_deleted', 'conflict')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagedSlackChildApps_DesiredManifest",
                table: "ManagedSlackChildApps",
                sql: "\"DesiredManifestVersion\" > 0 AND \"DesiredManifestHash\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagedSlackChildApps_IdentityPair",
                table: "ManagedSlackChildApps",
                sql: "\"BotUserId\" = '' OR \"AppId\" <> ''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManagedSlackChildApps_TransportKind",
                table: "ManagedSlackChildApps",
                sql: "\"TransportKind\" IN ('socket', 'https')");

            migrationBuilder.CreateIndex(
                name: "IX_SlackChildAppBindingObligations_AgentConnectionId",
                table: "SlackChildAppBindingObligations",
                column: "AgentConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_SlackChildAppBindingObligations_Status_UpdatedAt",
                table: "SlackChildAppBindingObligations",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackChildAppBindingObligations_ChildAppId",
                table: "SlackChildAppBindingObligations",
                column: "ChildAppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackOAuthAttempts_ChildAppId_Status_UpdatedAt",
                table: "SlackOAuthAttempts",
                columns: new[] { "ChildAppId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackOAuthAttempts_StateHash",
                table: "SlackOAuthAttempts",
                column: "StateHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SlackOAuthStates_SlackOAuthAttempts_AuthorizationAttemptId",
                table: "SlackOAuthStates",
                column: "AuthorizationAttemptId",
                principalTable: "SlackOAuthAttempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SlackOAuthStates_SlackOAuthAttempts_AuthorizationAttemptId",
                table: "SlackOAuthStates");

            migrationBuilder.DropTable(
                name: "SlackChildAppBindingObligations");

            migrationBuilder.DropTable(
                name: "SlackOAuthAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_Lifecycle",
                table: "SlackWorkspaceEnrollments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SlackWorkspaceEnrollments_ManagerCapability",
                table: "SlackWorkspaceEnrollments");

            migrationBuilder.DropIndex(
                name: "IX_SlackOAuthStates_AuthorizationAttemptId",
                table: "SlackOAuthStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SlackOAuthStates_Outcome",
                table: "SlackOAuthStates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagedSlackChildApps_AppliedManifestPair",
                table: "ManagedSlackChildApps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagedSlackChildApps_AppLifecycle",
                table: "ManagedSlackChildApps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagedSlackChildApps_Authorization",
                table: "ManagedSlackChildApps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagedSlackChildApps_BindingState",
                table: "ManagedSlackChildApps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagedSlackChildApps_DesiredManifest",
                table: "ManagedSlackChildApps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagedSlackChildApps_IdentityPair",
                table: "ManagedSlackChildApps");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ManagedSlackChildApps_TransportKind",
                table: "ManagedSlackChildApps");

            migrationBuilder.DropColumn(
                name: "AuthorizationAttemptId",
                table: "SlackOAuthStates");
        }
    }
}
