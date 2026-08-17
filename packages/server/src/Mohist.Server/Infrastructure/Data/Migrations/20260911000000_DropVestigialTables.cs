using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Drops AgentSubscriptions and RunnerWorks. The historical migrations
    /// that were meant to drop them (20260718090000_DropAgentSubscriptions,
    /// 20260730000000_DropRunnerWorksTable) were silently never applied: one
    /// lacked the [DbContext] attribute and the other both attributes, so EF
    /// Core's migration discovery skipped them. Databases upgraded through
    /// the old chain still carry these dead tables; IF EXISTS keeps this a
    /// no-op on databases created from the squashed baseline.
    /// </summary>
    [DbContext(typeof(MohistDbContext))]
    [Migration("20260911000000_DropVestigialTables")]
    public partial class DropVestigialTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS AgentSubscriptions;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS RunnerWorks;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Vestigial tables have no compatibility rollback.");
    }
}
