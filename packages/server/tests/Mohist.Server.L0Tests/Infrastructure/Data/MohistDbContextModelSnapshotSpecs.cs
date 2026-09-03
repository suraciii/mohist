using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Xunit;

namespace Mohist.Server.L0Tests.Infrastructure.Data;

[Trait("level", "L0")]
public class MohistDbContextModelSnapshotSpecs
{
    [Fact]
    public void Snapshot_matches_the_current_runtime_model()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new MohistDbContext(options);
        var differ = db.GetService<IMigrationsModelDiffer>();
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var operations = differ.GetDifferences(
            initializer.Initialize(new MohistDbContextModelSnapshot().Model, designTime: true).GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.Empty(operations);
    }
}
