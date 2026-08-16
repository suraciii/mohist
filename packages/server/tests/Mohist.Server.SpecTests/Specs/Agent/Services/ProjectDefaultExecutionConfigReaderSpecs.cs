using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

/// <summary>
/// Cost contract for the Project default execution configuration reader
/// (issue-560 T-001): one DB read per request scope, cached, so hydrating
/// Readiness for an N-agent list costs one read — not one read per Agent
/// and no Agent-domain grain call.
/// </summary>
public sealed class ProjectDefaultExecutionConfigReaderSpecs : IAsyncLifetime
{
    private TestSqliteDatabase _database = null!;

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GetAsync_PerformsOneReadPerScope_ForRepeatedCalls()
    {
        const string projectId = "proj-default-exec-reader";
        await SeedProjectAsync(projectId, new ExecutionConfigHint("pi", "openai/gpt-5.6", "high"));

        var commands = new SqlCommandCounter();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_database.ConnectionString)
            .AddInterceptors(commands)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var reader = new ProjectDefaultExecutionConfigReader(new TestDbContextFactory(options));

        var first = await reader.GetAsync(projectId);
        var second = await reader.GetAsync(projectId);
        var third = await reader.GetAsync(projectId);

        Assert.Equal(new ExecutionConfigHint("pi", "openai/gpt-5.6", "high"), first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(
            1,
            commands.CommandTexts.Count(command =>
                command.Contains("FROM \"Projects\"", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task GetAsync_CachesEachProjectWithinTheScope()
    {
        const string firstProjectId = "proj-default-exec-reader-first";
        const string secondProjectId = "proj-default-exec-reader-second";
        await SeedProjectAsync(firstProjectId, new ExecutionConfigHint("pi", "openai/gpt-5.6"));
        await SeedProjectAsync(secondProjectId, new ExecutionConfigHint("opencode", "anthropic/sonnet-4.6"));

        var commands = new SqlCommandCounter();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_database.ConnectionString)
            .AddInterceptors(commands)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var reader = new ProjectDefaultExecutionConfigReader(new TestDbContextFactory(options));

        var first = await reader.GetAsync(firstProjectId);
        var second = await reader.GetAsync(secondProjectId);
        var firstAgain = await reader.GetAsync(firstProjectId);

        Assert.Equal(new ExecutionConfigHint("pi", "openai/gpt-5.6"), first);
        Assert.Equal(new ExecutionConfigHint("opencode", "anthropic/sonnet-4.6"), second);
        Assert.Equal(first, firstAgain);
        Assert.Equal(
            2,
            commands.CommandTexts.Count(command =>
                command.Contains("FROM \"Projects\"", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForAProjectWithoutADefault()
    {
        const string projectId = "proj-default-exec-reader-unset";
        await SeedProjectAsync(projectId, null);

        var reader = new ProjectDefaultExecutionConfigReader(new TestDbContextFactory(
            new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_database.ConnectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options));

        Assert.Null(await reader.GetAsync(projectId));
    }

    private async Task SeedProjectAsync(string projectId, ExecutionConfigHint? config)
    {
        await using var db = _database.CreateContext();
        db.Projects.Add(new global::Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = "[]",
            DefaultExecutionConfigJson = ExecutionConfigJson.Serialize(config),
        });
        await db.SaveChangesAsync();
    }

    private sealed class SqlCommandCounter : DbCommandInterceptor
    {
        public List<string> CommandTexts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
