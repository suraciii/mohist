using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-479 T-004 / design D4: focused specs for the source-agnostic
/// unified session read surface (<c>/api/projects/{projectRef}/sessions</c>).
/// Verifies that <c>show</c> / <c>transcript</c> resolve by id for BOTH
/// sources (no <c>source-kind == agent-launch</c> gate), the unified list
/// delegates to the existing source-specific querier methods, the
/// <c>?run=</c> filter is project-scoped (no cross-project leak), and the
/// source-specific-field contract holds (workflow fields absent for
/// agent-launch and vice-versa). Handlers are tested directly — no HTTP,
/// no real process — via injected fakes + in-memory SQLite.
/// </summary>
public class UnifiedSessionRoutesSpecs
{
    private const string ProjectA = "proj-unified-A";
    private const string ProjectB = "proj-unified-B";
    private const string AgentId = "agent_unified";
    private const string AgentName = "unified-agent";
    private const string AgentLaunchSession = "s_unified_agent";
    private const string WorkflowSession = "s_unified_workflow";
    private const string WorkflowRunId = "wr-unified-1";
    private const string WorkflowSessionName = "coder";
    private const int WorkflowIssueNumber = 200;

    private static readonly DateTime CreatedAt = new(2026, 7, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    private static readonly ProjectInfo ProjectAInfo = new() { Id = ProjectA, Name = "unified-a" };
    private static readonly ProjectInfo ProjectBInfo = new() { Id = ProjectB, Name = "unified-b" };

    // ---------- show ----------

    [Fact]
    public async Task Show_AgentLaunchSession_ReturnsSummaryWithAgentFieldsOnly()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        Assert.Equal(AgentLaunchSession, data.GetProperty("id").GetString());
        Assert.Equal("agent-launch", data.GetProperty("source").GetString());
        Assert.Equal(AgentId, data.GetProperty("agentId").GetString());
        Assert.Equal(AgentName, data.GetProperty("agentName").GetString());
        Assert.False(data.TryGetProperty("workflowRunId", out _), "agent-launch session must not surface workflowRunId");
        Assert.False(data.TryGetProperty("sessionName", out _), "agent-launch session must not surface sessionName");
    }

    [Fact]
    public async Task Show_WorkflowSession_ReturnsSummaryWithWorkflowFieldsOnly()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, WorkflowSession, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        Assert.Equal(WorkflowSession, data.GetProperty("id").GetString());
        Assert.Equal("workflow", data.GetProperty("source").GetString());
        Assert.Equal(WorkflowRunId, data.GetProperty("workflowRunId").GetString());
        Assert.Equal(WorkflowSessionName, data.GetProperty("sessionName").GetString());
        Assert.False(data.TryGetProperty("agentId", out _), "workflow session must not surface agentId");
        Assert.False(data.TryGetProperty("agentName", out _), "workflow session must not surface agentName");
    }

    [Fact]
    public async Task Show_UnknownSessionId_Returns404()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, "never-existed", querier, CancellationToken.None);

        await AssertNotFoundAsync(result, "never-existed");
    }

    [Fact]
    public async Task Show_CrossProjectSession_Returns404()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectBInfo, AgentLaunchSession, querier, CancellationToken.None);

        await AssertNotFoundAsync(result, AgentLaunchSession);
    }

    [Fact]
    public async Task Show_AgentLaunchSourceContract_WorkflowFieldsAbsentOnWire()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        var json = data.GetRawText();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("workflowRunId", out _));
        Assert.False(doc.RootElement.TryGetProperty("sessionName", out _));
    }

    [Fact]
    public async Task Show_WorkflowSourceContract_AgentFieldsAbsentOnWire()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, WorkflowSession, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        var json = data.GetRawText();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("agentId", out _));
        Assert.False(doc.RootElement.TryGetProperty("agentName", out _));
    }

    // ---------- transcript ----------

    [Fact]
    public async Task Transcript_AgentLaunchSession_ReturnsTranscript()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectAInfo, AgentLaunchSession, runtimeSessionId: null, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        Assert.True(data.GetProperty("turns").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Transcript_WorkflowSession_ReturnsTranscript()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectAInfo, WorkflowSession, runtimeSessionId: null, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        Assert.True(data.GetProperty("turns").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Transcript_UnknownSessionId_Returns404()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectAInfo, "never-existed", runtimeSessionId: null, querier, CancellationToken.None);

        await AssertNotFoundAsync(result, "never-existed");
    }

    [Fact]
    public async Task Transcript_CrossProjectSession_Returns404()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);

        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectBInfo, AgentLaunchSession, runtimeSessionId: null, querier, CancellationToken.None);

        await AssertNotFoundAsync(result, AgentLaunchSession);
    }

    // ---------- list ----------

    [Fact]
    public async Task List_ByAgent_ReturnsAgentLaunchSessionsWithAgentFields()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);
        var agentQuerier = new AgentQuerier(db.Factory);

        var result = await UnifiedSessionRoutes.HandleListAsync(
            ProjectAInfo, agent: AgentId, issue: null, run: null, limit: null, agentQuerier, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        var items = data.EnumerateArray().ToList();
        Assert.True(items.Count >= 1);
        var item = items.First(i => i.GetProperty("id").GetString() == AgentLaunchSession);
        Assert.Equal("agent-launch", item.GetProperty("source").GetString());
        Assert.Equal(AgentId, item.GetProperty("agentId").GetString());
        Assert.False(item.TryGetProperty("workflowRunId", out _));
    }

    [Fact]
    public async Task List_ByAgent_UnknownAgent_Returns404()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);
        var agentQuerier = new AgentQuerier(db.Factory);

        var result = await UnifiedSessionRoutes.HandleListAsync(
            ProjectAInfo, agent: "agent_missing", issue: null, run: null, limit: null, agentQuerier, querier, CancellationToken.None);

        await AssertNotFoundAsync(result, "agent_missing");
    }

    [Fact]
    public async Task List_ByIssue_ReturnsWorkflowSessions()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);
        var agentQuerier = new AgentQuerier(db.Factory);

        var result = await UnifiedSessionRoutes.HandleListAsync(
            ProjectAInfo, agent: null, issue: WorkflowIssueNumber, run: null, limit: null, agentQuerier, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        var items = data.EnumerateArray().ToList();
        Assert.True(items.Count >= 1);
        var item = items.First(i => i.GetProperty("id").GetString() == WorkflowSession);
        Assert.Equal("workflow", item.GetProperty("source").GetString());
        Assert.Equal(WorkflowSessionName, item.GetProperty("sessionName").GetString());
        Assert.False(item.TryGetProperty("agentId", out _));
    }

    [Fact]
    public async Task List_ByRun_ReturnsWorkflowSessionsForProject()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);
        var agentQuerier = new AgentQuerier(db.Factory);

        var result = await UnifiedSessionRoutes.HandleListAsync(
            ProjectAInfo, agent: null, issue: null, run: WorkflowRunId, limit: null, agentQuerier, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        var ids = data.EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains(WorkflowSession, ids);
    }

    [Fact]
    public async Task List_ByRun_CrossProjectRun_YieldsEmpty_NoLeak()
    {
        var db = await BuildDbAsync(seedCrossProjectRun: true);
        var querier = CreateQuerier(db);
        var agentQuerier = new AgentQuerier(db.Factory);

        var result = await UnifiedSessionRoutes.HandleListAsync(
            ProjectAInfo, agent: null, issue: null, run: "wr-other-project", limit: null, agentQuerier, querier, CancellationToken.None);

        var data = await OkDataAsync(result);
        Assert.Equal(0, data.GetArrayLength());
    }

    [Fact]
    public async Task List_NoFilter_Returns400()
    {
        var db = await BuildDbAsync();
        var querier = CreateQuerier(db);
        var agentQuerier = new AgentQuerier(db.Factory);

        var result = await UnifiedSessionRoutes.HandleListAsync(
            ProjectAInfo, agent: null, issue: null, run: null, limit: null, agentQuerier, querier, CancellationToken.None);

        var (body, status) = await ExecuteAsync(result);
        Assert.Equal(400, status);
        Assert.Equal("session_filter_required", body.GetProperty("code").GetString());
    }

    // ---------- helpers ----------

    private static AgentSessionQuerier CreateQuerier(UnifiedTestDb db)
    {
        var sessionQuery = new AgentSessionQuery(db.Factory, TimeProvider);
        return new AgentSessionQuerier(db.Factory, sessionQuery);
    }

    private static async Task<UnifiedTestDb> BuildDbAsync(bool seedCrossProjectRun = false)
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);

        await SeedAgentAsync(factory);
        await SeedAgentLaunchSessionAsync(factory);
        await SeedWorkflowSessionAsync(factory, WorkflowSession, ProjectA, WorkflowRunId, WorkflowSessionName, WorkflowIssueNumber);
        await SeedTranscriptAsync(factory, AgentLaunchSession, "rt-agent");
        await SeedTranscriptAsync(factory, WorkflowSession, "rt-workflow");

        if (seedCrossProjectRun)
        {
            await SeedWorkflowSessionAsync(factory, "s_other_project", ProjectB, "wr-other-project", "coder", 300);
        }

        return new UnifiedTestDb(database, factory);
    }

    private static async Task SeedAgentAsync(IDbContextFactory<MohistDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        var domain = new DomainAgent
        {
            Id = AgentId,
            ProjectId = ProjectA,
            Name = AgentName,
            Status = AgentStatus.Active,
        };
        db.Agents.Add(new AgentRow
        {
            Id = AgentId,
            ProjectId = ProjectA,
            Name = AgentName,
            Status = AgentStatus.Active,
            State = AgentStore.Serialize(domain),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedAgentLaunchSessionAsync(IDbContextFactory<MohistDbContext> factory)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = AgentId,
            [GenericAgentSessionMetadata.AgentName] = AgentName,
        };

        var stateJson = JsonSerializer.Serialize(new
        {
            id = AgentLaunchSession,
            metadata = new { labels },
            runtime = new { runnerId = "runner-agent", workDir = (string?)null, runtime = "opencode" },
            settings = new { model = "gpt-4o" },
            status = new
            {
                agentRuntimeSessionId = (string?)"rt-agent",
                activity = "idle",
                createdAt = CreatedAt,
                lastDataAt = CreatedAt.AddMinutes(5),
            },
        }, JSON.Options);

        await using var db = factory.CreateDbContext();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = AgentLaunchSession,
            State = stateJson,
            RunnerId = "runner-agent",
            AgentSessionId = "rt-agent",
            Status = "opened",
            CreatedAt = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedWorkflowSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        string projectId,
        string workflowRunId,
        string sessionName,
        int issueNumber)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
            [AgentSessionQueryMetadataKeys.SessionName] = sessionName,
            [AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.ToString(),
        };

        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = "runner-" + sessionId, workDir = (string?)null, runtime = "opencode" },
            settings = new { model = "claude-3" },
            status = new
            {
                agentRuntimeSessionId = "rt-" + sessionId,
                activity = "idle",
                createdAt = CreatedAt,
                lastDataAt = CreatedAt.AddMinutes(8),
            },
        }, JSON.Options);

        await using var db = factory.CreateDbContext();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = stateJson,
            RunnerId = "runner-" + sessionId,
            AgentSessionId = "rt-" + sessionId,
            Status = "opened",
            CreatedAt = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedTranscriptAsync(IDbContextFactory<MohistDbContext> factory, string sessionId, string runtimeSessionId)
    {
        await using var db = factory.CreateDbContext();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = runtimeSessionId,
            Sequence = 1,
            StartedAt = CreatedAt,
            UpdatedAt = CreatedAt.AddMinutes(5),
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            TurnId = turn.Id,
            Sequence = 1,
            Type = TranscriptPartTypes.Input,
            CorrelationKey = "input",
            PayloadJson = JsonSerializer.Serialize(new { text = "hello" }, JSON.Options),
            LastSeenAt = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> OkDataAsync(IResult result)
    {
        var (body, status) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        return body.GetProperty("data");
    }

    private static async Task AssertNotFoundAsync(IResult result, string expectedFragment)
    {
        var (body, status) = await ExecuteAsync(result);
        Assert.Equal(404, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Contains(expectedFragment, body.GetProperty("error").GetString()!);
    }

    private static async Task<(JsonElement body, int status)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                foreach (var converter in JSON.Options.Converters)
                    o.SerializerOptions.Converters.Add(converter);
                o.SerializerOptions.DefaultIgnoreCondition = JSON.Options.DefaultIgnoreCondition;
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var element = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return (element, context.Response.StatusCode);
    }

    private sealed record UnifiedTestDb(TestSqliteDatabase Database, IDbContextFactory<MohistDbContext> Factory) : IDisposable
    {
        public void Dispose() => Database.Dispose();
    }
}
