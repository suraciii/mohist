using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration(MigrationId)]
public partial class AddAgentRetryReplyOrigin : Migration
{
    public const string MigrationId = "20260924000000_AddAgentRetryReplyOrigin";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ReplyProvenanceJson",
            table: "agent_retry_operations",
            type: "TEXT",
            maxLength: 4096,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ReplyProvenanceJson", table: "agent_retry_operations");
    }
}
