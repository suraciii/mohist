using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Mohist.Server.Infrastructure.Data.Migrations;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Domain;

public sealed class DropAgentSubscriptionsMigrationSpecs
{
    [Fact]
    public void Up_DropsAgentSubscriptionsTable()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        var migration = new DropAgentSubscriptions();
        var method = typeof(DropAgentSubscriptions).GetMethod(
            "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(migration, new object[] { builder });

        var operation = Assert.Single(builder.Operations);
        var drop = Assert.IsType<DropTableOperation>(operation);
        Assert.Equal("AgentSubscriptions", drop.Name);
    }
}
