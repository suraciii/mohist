using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Renames the legacy <c>active</c> epic status to <c>idle</c> in
    /// preparation for the autonomous-progression state machine (issue
    /// #263). After the rename:
    ///
    /// <list type="bullet">
    /// <item><description><c>idle</c> replaces <c>active</c> as the post-create status.</description></item>
    /// <item><description><c>running</c> is introduced for self-driving epics.</description></item>
    /// <item><description><c>paused</c>, <c>done</c>, <c>closed</c> are unchanged.</description></item>
    /// </list>
    ///
    /// No schema column change — the row already stores the status as a free
    /// string. Legacy <c>"active"</c> is also parsed to <c>Idle</c> at read
    /// time as a belt-and-suspenders safety net.
    /// </summary>
    public partial class EpicIdleRename : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Epics
                SET Status = 'idle'
                WHERE Status = 'active';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Epics
                SET Status = 'active'
                WHERE Status = 'idle';
                """);
        }
    }
}
