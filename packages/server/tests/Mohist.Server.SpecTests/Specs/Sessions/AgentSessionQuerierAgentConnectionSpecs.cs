using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Resolver + label-query coverage for sessions launched via the Slack DM
/// connection ingress. Companion to <see cref="AgentSessionQuerierTests"/>:
/// agent-connection is now a first-class source for follow-up / cancel /
/// stop routing, and its Slack identity labels are queryable.
/// </summary>
public sealed class AgentSessionQuerierAgentConnectionSpecs
{
    private const string ProjectId = "proj-dm-1";
    private const string OtherProject = "proj-dm-other";
    private const string AgentId = "agent-dm";
    private const string AgentName = "dm-agent";
    private const string RunnerId = "runner-dm";
    private const string ConnectionId = "connection_dm_1";
    private const string SlackUser = "U_OWNER";
    private const string DmConversation = "D1234";
    private const string OtherDmConversation = "D5678";
    private const string OtherConnection = "connection_dm_2";
    private const string OtherSlackUser = "U_OTHER";

    private static readonly DateTime CreatedAt = new(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc);
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ResolveCanonicalFollowupTarget_AgentConnectionSession_ReturnsTarget()
    {
        var (database, factory, sessionId) = await SeedAsync();
        await using var _ = database;
        var querier = NewQuerier(factory);

        var target = await querier.ResolveCanonicalFollowupTargetAsync(ProjectId, sessionId);

        Assert.NotNull(target);
        Assert.Equal(sessionId, target!.SessionId);
        Assert.Equal("agent-connection", target.SourceKind);
        Assert.Equal(RunnerId, target.RunnerId);
        Assert.Equal("opencode", target.Runtime);
        Assert.Equal($"rt-{sessionId}", target.RuntimeSessionId);
    }

    [Fact]
    public async Task ResolveCanonicalFollowupTarget_AgentConnection_CrossProject_ReturnsNull()
    {
        var (database, factory, sessionId) = await SeedAsync();
        await using var _ = database;
        var querier = NewQuerier(factory);

        var target = await querier.ResolveCanonicalFollowupTargetAsync(OtherProject, sessionId);
        Assert.Null(target);
    }

    [Fact]
    public async Task ResolveCancelTarget_AgentConnectionSession_ReturnsTarget()
    {
        var (database, factory, sessionId) = await SeedAsync();
        await using var _ = database;
        var querier = NewQuerier(factory);

        await using var db = factory.CreateDbContext();
        var storedLabels = await db.AgentSessions
            .Where(row => row.Id == sessionId)
            .Select(row => new { row.LabelProjectId, row.LabelSourceKind, row.LabelConnectionId, row.State })
            .FirstAsync();
        Assert.Equal(ProjectId, storedLabels.LabelProjectId);
        Assert.Equal("agent-connection", storedLabels.LabelSourceKind);
        Assert.Equal(ConnectionId, storedLabels.LabelConnectionId);

        var target = await querier.ResolveCancelTargetAsync(ProjectId, sessionId);

        Assert.NotNull(target);
        Assert.Equal(sessionId, target!.SessionId);
        Assert.Equal("agent-connection", target.SourceKind);
        Assert.Equal(RunnerId, target.RunnerId);
    }

    [Fact]
    public async Task QueryRowsByLabels_ConnectionId_FindsAgentConnectionSessions()
    {
        var (database, factory, _) = await SeedAsync();
        await using var _ = database;
        var query = new AgentSessionQuery(factory, TimeProvider);

        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = ProjectId,
                [AgentSessionQueryMetadataKeys.ConnectionId] = ConnectionId,
            });

        var ids = records.Select(record => record.Row.Id).ToList();
        Assert.Contains("session-conn-A", ids);
        Assert.Contains("session-conn-B", ids);
        Assert.DoesNotContain("session-workflow", ids);
    }

    [Fact]
    public async Task QueryRowsByLabels_SlackUserId_FiltersByOwner()
    {
        var (database, factory, _) = await SeedAsync();
        await using var _ = database;
        var query = new AgentSessionQuery(factory, TimeProvider);

        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = ProjectId,
                [AgentSessionQueryMetadataKeys.SlackUserId] = SlackUser,
            });

        var ids = records.Select(record => record.Row.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.All(records, record =>
            Assert.Equal(SlackUser, record.Label(AgentSessionQueryMetadataKeys.SlackUserId)));
    }

    [Fact]
    public async Task QueryRowsByLabels_SlackConversationId_FiltersByConversation()
    {
        var (database, factory, _) = await SeedAsync();
        await using var _ = database;
        var query = new AgentSessionQuery(factory, TimeProvider);

        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = ProjectId,
                [AgentSessionQueryMetadataKeys.SlackConversationId] = DmConversation,
            });

        Assert.Single(records);
        Assert.Equal("session-conn-A", records[0].Row.Id);
    }

    private static AgentSessionQuerier NewQuerier(IDbContextFactory<MohistDbContext> factory)
    {
        var sessionQuery = new AgentSessionQuery(factory, TimeProvider);
        return new AgentSessionQuerier(factory, sessionQuery);
    }

    private static async Task<(TestSqliteDatabase Database, IDbContextFactory<MohistDbContext> Factory, string SessionId)> SeedAsync()
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);

        var labelsAgentConnA = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [GenericAgentSessionMetadata.AgentId] = AgentId,
            [GenericAgentSessionMetadata.AgentName] = AgentName,
            [AgentSessionQueryMetadataKeys.ConnectionId] = ConnectionId,
            [AgentSessionQueryMetadataKeys.SlackUserId] = SlackUser,
            [AgentSessionQueryMetadataKeys.SlackConversationId] = DmConversation,
        };

        var labelsAgentConnB = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [GenericAgentSessionMetadata.AgentId] = AgentId,
            [GenericAgentSessionMetadata.AgentName] = AgentName,
            [AgentSessionQueryMetadataKeys.ConnectionId] = ConnectionId,
            [AgentSessionQueryMetadataKeys.SlackUserId] = SlackUser,
            [AgentSessionQueryMetadataKeys.SlackConversationId] = OtherDmConversation,
        };

        var labelsOtherUser = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-connection",
            [GenericAgentSessionMetadata.AgentId] = AgentId,
            [GenericAgentSessionMetadata.AgentName] = AgentName,
            [AgentSessionQueryMetadataKeys.ConnectionId] = OtherConnection,
            [AgentSessionQueryMetadataKeys.SlackUserId] = OtherSlackUser,
            [AgentSessionQueryMetadataKeys.SlackConversationId] = "D9999",
        };

        var labelsWorkflow = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = "wr-1",
            [AgentSessionQueryMetadataKeys.SessionName] = "coder",
        };

        await InsertSessionAsync(factory, "session-conn-A", labelsAgentConnA, "rt-session-conn-A");
        await InsertSessionAsync(factory, "session-conn-B", labelsAgentConnB, "rt-session-conn-B");
        await InsertSessionAsync(factory, "session-other-user", labelsOtherUser, "rt-session-other-user");
        await InsertSessionAsync(factory, "session-workflow", labelsWorkflow, "rt-session-workflow");

        return (database, factory, "session-conn-A");
    }

    private static async Task InsertSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        IReadOnlyDictionary<string, string> labels,
        string runtimeSessionId)
    {
        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = RunnerId, workDir = (string?)null, runtime = "opencode" },
            settings = new { model = "gpt-4o" },
            status = new
            {
                agentRuntimeSessionId = runtimeSessionId,
                activity = "idle",
                createdAt = CreatedAt,
                lastDataAt = CreatedAt.AddMinutes(5),
            },
        }, JSON.Options);

        await using var db = factory.CreateDbContext();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = stateJson,
            RunnerId = RunnerId,
            AgentSessionId = runtimeSessionId,
            Status = "bound",
            CreatedAt = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
