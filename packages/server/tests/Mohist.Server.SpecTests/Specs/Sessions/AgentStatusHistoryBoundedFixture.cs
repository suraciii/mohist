using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Integration fixture for the issue-467 history-bounded status
/// selection specs. Hosted in its own xUnit collection
/// (<c>AgentStatusHistoryBounded</c>) so the counting
/// <see cref="WorkflowQuerier"/> substitution never leaks into the
/// shared <c>IntegrationSessions</c> collection. Sharing the regular
/// <see cref="MohistIntegrationFixture"/> would mean other tests in
/// the collection would resolve the counting querier, breaking their
/// assertions on real Workflow state.
/// </summary>
public sealed class AgentStatusHistoryBoundedFixture : IAsyncLifetime
{
    private readonly CountingWorkflowWebApplicationFactory _factory;
    private readonly string _connectionString;
    private SqliteConnection _keeper = null!;

    public AgentStatusHistoryBoundedFixture()
    {
        _connectionString = $"Data Source=agent-status-bounded-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _factory = new CountingWorkflowWebApplicationFactory(
            _connectionString,
            "/mohist-tests/agent-status-bounded/runner",
            "/mohist-tests/agent-status-bounded/system-update.json",
            "/mohist-tests/agent-status-bounded/logs",
            TimeProvider);
    }

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public CountingWorkflowQuerier CountingWorkflowQuerier =>
        _factory.CountingWorkflowQuerier
            ?? throw new InvalidOperationException("CountingWorkflowQuerier is initialized on first scope resolution; tests must run after the host has started.");
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    public async ValueTask InitializeAsync()
    {
        _keeper = new SqliteConnection(_connectionString);
        await _keeper.OpenAsync();
        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {MohistIntegrationFixture.OperatorToken}");
        await _factory.EnsureSchemaAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _factory.Dispose();
        if (_keeper is not null) await _keeper.DisposeAsync();
    }

    private sealed class CountingWorkflowWebApplicationFactory : MohistWebApplicationFactory
    {
        public CountingWorkflowQuerier? CountingWorkflowQuerier;

        public CountingWorkflowWebApplicationFactory(
            string connectionString,
            string runnerRoot,
            string systemUpdateStatePath,
            string logsPath,
            FakeTimeProvider timeProvider)
            : base(connectionString, runnerRoot, systemUpdateStatePath, logsPath, timeProvider)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<WorkflowQuerier>();
                services.AddScoped<CountingWorkflowQuerier>(provider =>
                {
                    // Lazily construct a single per-fixture
                    // CountingWorkflowQuerier once the DI tree is
                    // available, then return it for every scope so
                    // call counts persist across requests.
                    return CountingWorkflowQuerier ??=
                        new CountingWorkflowQuerier(
                            provider.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
                            provider.GetRequiredService<Mohist.Server.Workflow.Services.WorkflowDefinitionResolver>(),
                            provider.GetRequiredService<Mohist.Server.Workflow.Services.WorkflowVariableResolver>(),
                            provider.GetRequiredService<IWorkflowArtifactQuerier>());
                });
                services.AddScoped<WorkflowQuerier>(provider =>
                    provider.GetRequiredService<CountingWorkflowQuerier>());
            });
        }
    }
}

/// <summary>
/// Counts <see cref="WorkflowQuerier.GetStatusAsync"/> invocations and
/// hands back deterministic <see cref="WorkflowStatusView"/> snapshots
/// when the test pre-configures them via <see cref="SetStatus"/>.
/// Acts as a test-only replacement for the real WorkflowQuerier so
/// deterministic specs can assert de-duplication of status reads and
/// the post-selection pending-work match without depending on
/// workflow grain state.
/// </summary>
public sealed class CountingWorkflowQuerier : WorkflowQuerier
{
    private readonly ConcurrentDictionary<string, WorkflowStatusView?> _statuses =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _statusCalls =
        new(StringComparer.Ordinal);

    public CountingWorkflowQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        Mohist.Server.Workflow.Services.WorkflowDefinitionResolver definitionResolver,
        Mohist.Server.Workflow.Services.WorkflowVariableResolver variableResolver,
        IWorkflowArtifactQuerier artifactQuerier)
        : base(
            dbFactory,
            definitionResolver,
            variableResolver,
            artifactQuerier,
            new WorkflowRunStatusCache(),
            new WorkflowRunDeserializer())
    {
    }

    public void SetStatus(string workflowRunId, WorkflowStatusView? view) =>
        _statuses[workflowRunId] = view;

    public int GetStatusCallCount(string workflowRunId) =>
        _statusCalls.TryGetValue(workflowRunId, out var count) ? count : 0;

    public override Task<WorkflowStatusView?> GetStatusAsync(string workflowRunId)
    {
        _statusCalls.AddOrUpdate(workflowRunId, 1, (_, current) => current + 1);
        if (_statuses.TryGetValue(workflowRunId, out var configured))
        {
            return Task.FromResult<WorkflowStatusView?>(configured);
        }
        return Task.FromResult<WorkflowStatusView?>(null);
    }
}
