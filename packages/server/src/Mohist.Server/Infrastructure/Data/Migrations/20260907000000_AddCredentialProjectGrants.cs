using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Adds the credential-owned Project boundary for direct external Agent PATs.
/// The nullable kind preserves older PATs as control-plane-only credentials;
/// no existing integration ProjectId binding is changed.
/// </summary>
public partial class AddCredentialProjectGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DirectApiProjectGrantKind",
            table: "Credentials",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "CredentialProjectGrants",
            columns: table => new
            {
                CredentialId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CredentialProjectGrants", x => new { x.CredentialId, x.ProjectId });
                table.ForeignKey(
                    name: "FK_CredentialProjectGrants_Credentials_CredentialId",
                    column: x => x.CredentialId,
                    principalTable: "Credentials",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CredentialProjectGrants_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CredentialProjectGrants_ProjectId",
            table: "CredentialProjectGrants",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "UX_CredentialProjectGrants_CredentialId_ProjectId",
            table: "CredentialProjectGrants",
            columns: new[] { "CredentialId", "ProjectId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CredentialProjectGrants");

        // Keep the nullable column during historical SQLite rollbacks. Older
        // migrations rebuild Credentials while moving backward and use the
        // latest model shape; dropping this column first makes those rebuilds
        // fail before they reach their own target. The column is inert without
        // the child table and is retained only for that historical rollback
        // compatibility.
    }
}
