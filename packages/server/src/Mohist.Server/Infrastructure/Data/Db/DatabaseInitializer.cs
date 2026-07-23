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
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await db.Database.MigrateAsync(cancellationToken);
        await ProjectRepositoryDataUpgrader.UpgradeAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await WorkflowProfileDataUpgrader.UpgradeAsync(db, cancellationToken, persistChanges: false);
            var migration = await WorkflowProfileDataMigrator.MigrateAsync(db, timeProvider, cancellationToken);
            if (migration.Diagnostics.Count > 0)
            {
                throw new InvalidOperationException(
                    "WorkflowProfile migration completed with diagnostics:\n"
                    + string.Join("\n", migration.Diagnostics));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
