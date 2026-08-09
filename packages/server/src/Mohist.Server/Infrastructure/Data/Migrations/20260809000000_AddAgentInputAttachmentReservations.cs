using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260809000000_AddAgentInputAttachmentReservations")]
public partial class AddAgentInputAttachmentReservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AgentInputAttachmentReservations",
            columns: table => new
            {
                ReservationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                AttachmentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_AgentInputAttachmentReservations",
                    x => new { x.ReservationId, x.AttachmentId });
            });

        migrationBuilder.CreateIndex(
            name: "IX_AgentInputAttachmentReservations_Attachment",
            table: "AgentInputAttachmentReservations",
            columns: new[] { "ProjectId", "AttachmentId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentInputAttachmentReservations_Reservation",
            table: "AgentInputAttachmentReservations",
            columns: new[] { "ProjectId", "ReservationId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_AgentInputAttachmentReservations_Expiry",
            table: "AgentInputAttachmentReservations",
            columns: new[] { "ProjectId", "Status", "ExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "AgentInputAttachmentReservations");
}
