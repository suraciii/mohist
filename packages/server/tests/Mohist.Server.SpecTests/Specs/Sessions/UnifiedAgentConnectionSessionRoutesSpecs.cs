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

public sealed class UnifiedAgentConnectionSessionRoutesSpecs
{
    private const string ProjectA = "proj-unified-connection-A";
    private const string ProjectB = "proj-unified-connection-B";
    private const string AgentId = "agent_unified_connection";
    private const string AgentName = "unified-connection-agent";
    private const string AgentLaunchSession = "s_unified_connection_launch";
    private const string AgentConnectionSession = "s_unified_connection_slack";
    private const string UnknownSourceSession = "s_unified_connection_unknown";

    private static readonly DateTime CreatedAt = new(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
    private static readonly ProjectInfo ProjectAInfo = new() { Id = ProjectA, Name = "unified-connection-a" };
    private static readonly ProjectInfo ProjectBInfo = new() { Id = ProjectB, Name = "unified-connection-b" };

    [Fact]
    public async Task Show_AgentConnectionSession_ReturnsSourceIdentityRuntimeAndObservations()
    {
        using var database = await BuildDbAsync();

        var result = await UnifiedSessionRoutes.HandleShowAsync(
            ProjectAInfo,
            AgentConnectionSession,
            CreateQuerier(database),
            CancellationToken.None);

        var data = await OkDataAsync(result);
        Assert.Equal(AgentConnectionSession, data.GetProperty("id").GetString());
        Assert.Equal("agent-connection", data.GetProperty("source").GetString());
        Assert.Equal(AgentId, data.GetProperty("agentId").GetString());
        Assert.Equal(AgentName, data.GetProperty("agentName").GetString());
        Assert.Equal("rt-unified-connection", data.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", data.GetProperty("runtime").GetString());
        Assert.Equal("idle", data.GetProperty("activity").GetString());
        Assert.Single(data.GetProperty("inputs").EnumerateArray());
        Assert.Single(data.GetProperty("turns").EnumerateArray());
        Assert.False(data.TryGetProperty("workflowRunId", out _));
        Assert.False(data.TryGetProperty("sessionName", out _));
    }

    [Fact]
    public async Task Transcript_AgentConnectionSession_ReturnsPersistedTranscript()
    {
        using var database = await BuildDbAsync();

        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectAInfo,
            AgentConnectionSession,
            runtimeSessionId: null,
            CreateQuerier(database),
            CancellationToken.None);

        var data = await OkDataAsync(result);
        var turn = Assert.Single(data.GetProperty("turns").EnumerateArray());
        Assert.Equal("hello from Slack", turn.GetProperty("user").GetProperty("text").GetString());
    }

    [Fact]
    public async Task List_ByAgent_IncludesAgentLaunchAndAgentConnectionSessions()
    {
        using var database = await BuildDbAsync();
        var result = await UnifiedSessionRoutes.HandleListAsync(
            ProjectAInfo,
            agent: AgentId,
            issue: null,
            run: null,
            limit: null,
            new AgentQuerier(database.Factory),
            CreateQuerier(database),
            CancellationToken.None);

        var items = (await OkDataAsync(result)).EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var launch = items.Single(item => item.GetProperty("id").GetString() == AgentLaunchSession);
        Assert.Equal("agent-launch", launch.GetProperty("source").GetString());

        var connection = items.Single(item => item.GetProperty("id").GetString() == AgentConnectionSession);
        Assert.Equal("agent-connection", connection.GetProperty("source").GetString());
        Assert.Equal(AgentId, connection.GetProperty("agentId").GetString());
        Assert.Equal(AgentName, connection.GetProperty("agentName").GetString());
        Assert.Equal("rt-unified-connection", connection.GetProperty("runtimeSessionId").GetString());
    }

    [Fact]
    public async Task Show_AgentConnectionSession_FromAnotherProject_Returns404()
    {
        using var database = await BuildDbAsync();

        var result = await UnifiedSessionRoutes.HandleShowAsync(
            ProjectBInfo,
            AgentConnectionSession,
            CreateQuerier(database),
            CancellationToken.None);

        await AssertNotFoundAsync(result, AgentConnectionSession);
    }

    [Fact]
    public async Task Transcript_AgentConnectionSession_FromAnotherProject_Returns404()
    {
        using var database = await BuildDbAsync();

        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectBInfo,
            AgentConnectionSession,
            runtimeSessionId: null,
            CreateQuerier(database),
            CancellationToken.None);

        await AssertNotFoundAsync(result, AgentConnectionSession);
    }

    [Fact]
    public async Task Show_UnknownSource_Returns404()
    {
        using var database = await BuildDbAsync();

        var result = await UnifiedSessionRoutes.HandleShowAsync(
            ProjectAInfo,
            UnknownSourceSession,
            CreateQuerier(database),
            CancellationToken.None);

        await AssertNotFoundAsync(result, UnknownSourceSession);
    }

    [Fact]
    public async Task Transcript_UnknownSource_Returns404()
    {
        using var database = await BuildDbAsync();

        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectAInfo,
            UnknownSourceSession,
            runtimeSessionId: null,
            CreateQuerier(database),
            CancellationToken.None);

        await AssertNotFoundAsync(result, UnknownSourceSession);
    }

    private static AgentSessionQuerier CreateQuerier(UnifiedAgentConnectionTestDb database) =>
        new(database.Factory, new AgentSessionQuery(database.Factory, TimeProvider));

    private static async Task<UnifiedAgentConnectionTestDb> BuildDbAsync()
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedAgentAsync(factory);
        await SeedSessionAsync(factory, AgentLaunchSession, "agent-launch", "rt-launch", CreatedAt);
        await SeedSessionAsync(factory, AgentConnectionSession, "agent-connection", "rt-unified-connection", CreatedAt.AddMinutes(1), true);
        await SeedSessionAsync(factory, UnknownSourceSession, "future-source", "rt-unknown", CreatedAt.AddMinutes(2));
        await SeedTranscriptAsync(factory, AgentConnectionSession, "rt-unified-connection");
        return new UnifiedAgentConnectionTestDb(database, factory);
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

    private static async Task SeedSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        string sourceKind,
        string runtimeSessionId,
        DateTime createdAt,
        bool includeObservations = false)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [AgentSessionQueryMetadataKeys.SourceKind] = sourceKind,
        };
        if (sourceKind is "agent-launch" or "agent-connection")
        {
            labels[GenericAgentSessionMetadata.AgentId] = AgentId;
            labels[GenericAgentSessionMetadata.AgentName] = AgentName;
        }
        if (sourceKind == "agent-connection")
        {
            labels[AgentSessionQueryMetadataKeys.ConnectionId] = "connection-unified";
            labels[AgentSessionQueryMetadataKeys.SlackConversationId] = "D-unified";
            labels[AgentSessionQueryMetadataKeys.SlackThreadTs] = "1710000000.000001";
        }

        var inputs = includeObservations
            ? new object[]
            {
                new
                {
                    id = "input-unified-connection",
                    sequence = 1L,
                    text = "hello from Slack",
                    source = "slack",
                    acceptance = "accepted",
                    recordedAt = createdAt,
                    provenance = new
                    {
                        providerKind = "slack",
                        workspaceId = "T-unified",
                        conversationId = "D-unified",
                        threadId = "1710000000.000001",
                        memberId = "U-unified",
                        messageId = "m-unified",
                        connectionId = "connection-unified",
                    },
                },
            }
            : Array.Empty<object>();
        var turns = includeObservations
            ? new object[]
            {
                new
                {
                    id = "turn-unified-connection",
                    sequence = 1L,
                    inputIds = new[] { "input-unified-connection" },
                    status = "completed",
                    result = new { message = "completed" },
                    recordedAt = createdAt,
                    updatedAt = createdAt.AddMinutes(5),
                },
            }
            : Array.Empty<object>();
        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = "runner-" + sessionId, workDir = (string?)null, runtime = "opencode" },
            settings = new { model = "gpt-4o" },
            status = new
            {
                agentRuntimeSessionId = runtimeSessionId,
                activity = "idle",
                createdAt,
                lastDataAt = createdAt.AddMinutes(5),
                inputs,
                turns,
            },
        }, JSON.Options);

        await using var db = factory.CreateDbContext();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = stateJson,
            RunnerId = "runner-" + sessionId,
            AgentSessionId = runtimeSessionId,
            Status = "opened",
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedTranscriptAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        string runtimeSessionId)
    {
        await using var db = factory.CreateDbContext();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = runtimeSessionId,
            Sequence = 1,
            PromptText = "hello from Slack",
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
            PayloadJson = JsonSerializer.Serialize(new { text = "hello from Slack" }, JSON.Options),
            FirstSeenAt = CreatedAt,
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
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                foreach (var converter in JSON.Options.Converters)
                    options.SerializerOptions.Converters.Add(converter);
                options.SerializerOptions.DefaultIgnoreCondition = JSON.Options.DefaultIgnoreCondition;
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var element = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return (element, context.Response.StatusCode);
    }

    private sealed record UnifiedAgentConnectionTestDb(
        TestSqliteDatabase Database,
        IDbContextFactory<MohistDbContext> Factory) : IDisposable
    {
        public void Dispose() => Database.Dispose();
    }
}
