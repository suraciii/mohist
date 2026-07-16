using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260716120000_AddRuntimeSessionIdToTranscriptTurns")]
    public partial class AddRuntimeSessionIdToTranscriptTurns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RuntimeSessionId",
                table: "AgentSessionTranscriptTurns",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionTranscriptTurns_SessionId_RuntimeSessionId_Sequence",
                table: "AgentSessionTranscriptTurns",
                columns: new[] { "SessionId", "RuntimeSessionId", "Sequence" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentSessionTranscriptTurns_SessionId_RuntimeSessionId_Sequence",
                table: "AgentSessionTranscriptTurns");

            migrationBuilder.DropColumn(
                name: "RuntimeSessionId",
                table: "AgentSessionTranscriptTurns");
        }
    }
}
