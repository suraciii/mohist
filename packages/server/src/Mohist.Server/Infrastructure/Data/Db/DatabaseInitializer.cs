using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Infrastructure.Data.Db;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await ProjectRepositoryDataUpgrader.UpgradeAsync(db, cancellationToken);
        await WorkflowProfileDataUpgrader.UpgradeAsync(db, cancellationToken);
    }
}
