using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public abstract class WorkflowActivityHistoryTestSupport : IAsyncLifetime
{
    private TestSqliteDatabase? _database;

    protected FakeTimeProvider TimeProvider { get; } = new(
        new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
    protected IDbContextFactory<MohistDbContext> DbFactory { get; private set; } = null!;
    protected AgentSessionQuery SessionQuery { get; private set; } = null!;
    protected CountingWorkflowStatusReader WorkflowStatuses { get; } = new();

    public ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        DbFactory = new TestDbContextFactory(_database.Options);
        SessionQuery = new AgentSessionQuery(DbFactory, TimeProvider);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }

    protected WorkflowActivityQuerier CreateQuerier(AgentSessionQuery? sessionQuery = null) =>
        new(DbFactory, WorkflowStatuses, sessionQuery ?? SessionQuery);

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}

public sealed class CountingWorkflowStatusReader : IWorkflowStatusReader
{
    private readonly ConcurrentDictionary<string, WorkflowStatusView> _statuses =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _statusCalls =
        new(StringComparer.Ordinal);

    public void SetStatus(string workflowRunId, WorkflowStatusView view) =>
        _statuses[workflowRunId] = view;

    public int GetStatusCallCount(string workflowRunId) =>
        _statusCalls.TryGetValue(workflowRunId, out var count) ? count : 0;

    public Task<WorkflowStatusView?> GetStatusAsync(string workflowRunId)
    {
        _statusCalls.AddOrUpdate(workflowRunId, 1, (_, current) => current + 1);
        return Task.FromResult(
            _statuses.TryGetValue(workflowRunId, out var configured)
                ? configured
                : null);
    }
}
