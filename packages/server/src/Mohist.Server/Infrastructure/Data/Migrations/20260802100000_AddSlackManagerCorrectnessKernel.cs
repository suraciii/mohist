using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSlackManagerCorrectnessKernel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SlackWorkspaceEnrollments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ManagerExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Lifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ManagerCapability = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CapabilityReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PlanCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManagedAppLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    ManagerCredentialRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AuditJson = table.Column<string>(type: "JSON", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackWorkspaceEnrollments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManagedSlackChildApps",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentConnectionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PublicIngressBaseUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BotUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppLifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Authorization = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TransportKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DesiredManifestVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DesiredManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AppliedManifestVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    AppliedManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    VerifiedScopesJson = table.Column<string>(type: "JSON", nullable: false),
                    OperationFence = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OperationKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    OperationStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UnknownOutcome = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ErrorClass = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AuthorizationAttemptId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AuthorizationExpiresAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClientSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SigningSecretRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AppLevelTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    BotTokenRef = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    BindingState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BindingErrorClass = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AuditJson = table.Column<string>(type: "JSON", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedSlackChildApps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManagedSlackChildApps_AgentConnections_AgentConnectionId",
                        column: x => x.AgentConnectionId,
                        principalTable: "AgentConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManagedSlackChildApps_SlackWorkspaceEnrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "SlackWorkspaceEnrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlackOAuthStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ChildAppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkspaceTeamId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlackOAuthStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlackOAuthStates_ManagedSlackChildApps_ChildAppId",
                        column: x => x.ChildAppId,
                        principalTable: "ManagedSlackChildApps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AgentConnections_StagedSlackBinding",
                table: "AgentConnections",
                sql: "(\"AppId\" = '' AND \"BotUserId\" = '') OR (\"AppId\" <> '' AND \"BotUserId\" <> '')");

            migrationBuilder.CreateIndex(
                name: "IX_ManagedSlackChildApps_EnrollmentId_UpdatedAt",
                table: "ManagedSlackChildApps",
                columns: new[] { "EnrollmentId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ManagedSlackChildApps_AgentConnectionId",
                table: "ManagedSlackChildApps",
                column: "AgentConnectionId",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ManagedSlackChildApps_WorkspaceTeamId_AppId",
                table: "ManagedSlackChildApps",
                columns: new[] { "WorkspaceTeamId", "AppId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"AppId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_SlackOAuthStates_ChildAppId_ConsumedAt_ExpiresAt",
                table: "SlackOAuthStates",
                columns: new[] { "ChildAppId", "ConsumedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackOAuthStates_StateHash",
                table: "SlackOAuthStates",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlackWorkspaceEnrollments_Lifecycle_UpdatedAt",
                table: "SlackWorkspaceEnrollments",
                columns: new[] { "Lifecycle", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SlackWorkspaceEnrollments_WorkspaceTeamId",
                table: "SlackWorkspaceEnrollments",
                column: "WorkspaceTeamId",
                unique: true,
                filter: "\"DeletedAt\" IS NULL AND \"Lifecycle\" = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SlackOAuthStates");

            migrationBuilder.DropTable(
                name: "ManagedSlackChildApps");

            migrationBuilder.DropTable(
                name: "SlackWorkspaceEnrollments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AgentConnections_StagedSlackBinding",
                table: "AgentConnections");
        }
    }
}
