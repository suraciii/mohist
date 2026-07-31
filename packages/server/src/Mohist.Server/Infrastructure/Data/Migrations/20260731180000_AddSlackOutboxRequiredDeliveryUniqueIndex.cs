using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class AddSlackOutboxRequiredDeliveryUniqueIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "UX_SlackOutboxRows_ConnectionId_DispatchRef_Kind",
            table: "SlackOutboxRows",
            columns: new[] { "ConnectionId", "DispatchRef", "Kind" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropIndex(
            name: "UX_SlackOutboxRows_ConnectionId_DispatchRef_Kind",
            table: "SlackOutboxRows");
}
