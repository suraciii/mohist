using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

/// <summary>
/// Persistent auth audit trail. Every
/// recorded event carries subject, event type, target, time and
/// non-secret metadata; token values are never stored.
/// </summary>
public partial class AddAuthAuditEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuthAuditEvents",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                SubjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TargetKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TargetId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                MetadataJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthAuditEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuthAuditEvents_EventType_OccurredAt",
            table: "AuthAuditEvents",
            columns: new[] { "EventType", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuthAuditEvents");
    }
}
