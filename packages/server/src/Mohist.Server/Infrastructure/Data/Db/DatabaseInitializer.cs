using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var logger = scope.ServiceProvider
            .GetService<ILoggerFactory>()?
            .CreateLogger(nameof(WorkflowRunStateDataUpgrader));
        await db.Database.MigrateAsync(cancellationToken);
        await ProjectRepositoryDataUpgrader.UpgradeAsync(db, cancellationToken);
        await WorkflowRunStateDataUpgrader.UpgradeAsync(db, cancellationToken, logger: logger);
        var workProjectionLogger = scope.ServiceProvider
            .GetService<ILoggerFactory>()?
            .CreateLogger(nameof(WorkflowRunWorkProjectionDataUpgrader));
        await WorkflowRunWorkProjectionDataUpgrader.UpgradeAsync(db, cancellationToken, logger: workProjectionLogger);
        var dispatchSnapshotLogger = scope.ServiceProvider
            .GetService<ILoggerFactory>()?
            .CreateLogger(nameof(WorkflowDispatchSnapshotDataUpgrader));
        await WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync(db, cancellationToken, logger: dispatchSnapshotLogger);
        await WorkflowDispatchSnapshotDataUpgrader.SweepOrphansAsync(db, cancellationToken, logger: dispatchSnapshotLogger);
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
