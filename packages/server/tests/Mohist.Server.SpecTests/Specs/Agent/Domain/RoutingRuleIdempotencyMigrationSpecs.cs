using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Mohist.Server.Infrastructure.Data.Migrations;
using System.Reflection;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Domain;

public sealed class RoutingRuleIdempotencyMigrationSpecs
{
    [Fact]
    public void Up_AddsNullableColumnAndFilteredUniqueIndex()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        Invoke("Up", builder);

        var nameIndexDrop = Assert.IsType<DropIndexOperation>(Assert.Single(builder.Operations, operation =>
            operation is DropIndexOperation { Name: "UX_RoutingRules_ProjectId_Name" }));
        Assert.Equal("RoutingRules", nameIndexDrop.Table);

        var nameIndex = Assert.IsType<CreateIndexOperation>(Assert.Single(builder.Operations, operation =>
            operation is CreateIndexOperation { Name: "UX_RoutingRules_ProjectId_Name" }));
        Assert.Equal("RoutingRules", nameIndex.Table);
        Assert.Equal(new[] { "ProjectId", "Name" }, nameIndex.Columns);
        Assert.True(nameIndex.IsUnique);
        Assert.Equal("\"Status\" <> 'deleted'", nameIndex.Filter);

        var column = Assert.IsType<AddColumnOperation>(Assert.Single(builder.Operations, operation => operation is AddColumnOperation));
        Assert.Equal("RoutingRules", column.Table);
        Assert.Equal("IdempotencyKey", column.Name);
        Assert.Equal(typeof(string), column.ClrType);
        Assert.True(column.IsNullable);
        Assert.Equal(256, column.MaxLength);

        var index = Assert.IsType<CreateIndexOperation>(Assert.Single(builder.Operations, operation => operation is CreateIndexOperation));
        Assert.Equal("UX_RoutingRules_ProjectId_IdempotencyKey", index.Name);
        Assert.Equal(new[] { "ProjectId", "IdempotencyKey" }, index.Columns);
        Assert.True(index.IsUnique);
        Assert.Equal("\"IdempotencyKey\" IS NOT NULL", index.Filter);
    }

    [Fact]
    public void Down_RejectsLossOfExistingIdempotencyFactsBeforeAnyDrop()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        var exception = Assert.Throws<TargetInvocationException>(() => Invoke("Down", builder));

        Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Empty(builder.Operations);
    }

    private static void Invoke(string methodName, MigrationBuilder builder)
    {
        var method = typeof(AddRoutingRuleIdempotencyKey).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(new AddRoutingRuleIdempotencyKey(), new object[] { builder });
    }
}
