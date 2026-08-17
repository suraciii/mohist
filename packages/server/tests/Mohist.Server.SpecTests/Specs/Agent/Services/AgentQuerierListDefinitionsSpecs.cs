using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public sealed class AgentQuerierListDefinitionsSpecs : IAsyncLifetime
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
    public async Task ListActiveDefinitions_DoesNotHydrateReadinessFromAgentJobHistory()
    {
        const string projectId = "proj-active-definitions";
        const string agentId = "agent-active-definition";
        await using (var db = _database.CreateContext())
        {
            db.Agents.Add(new AgentRow
            {
                Id = agentId,
                ProjectId = projectId,
                Status = AgentStatus.Active,
                State = JsonSerializer.Serialize(new DomainAgent
                {
                    Id = agentId,
                    ProjectId = projectId,
                    Name = "Availability Agent",
                    Status = AgentStatus.Active,
                    Instructions = "Work carefully.",
                }, JSON.Options),
            });
            await db.SaveChangesAsync();
        }

        var commands = new SqlCommandCounter();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_database.ConnectionString)
            .AddInterceptors(commands)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var factory = new TestDbContextFactory(options);
        var readiness = new AgentReadinessService(
            new AgentJobQuerier(factory),
            new ProjectDefaultExecutionConfigReader(factory));
        var querier = new AgentQuerier(factory, readiness);

        var agents = await querier.ListActiveDefinitionsAsync(projectId);

        var agent = Assert.Single(agents);
        Assert.Equal(agentId, agent.Id);
        Assert.Null(agent.Executability);
        Assert.DoesNotContain(commands.CommandTexts, command =>
            command.Contains("AgentJobs", StringComparison.OrdinalIgnoreCase));
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
