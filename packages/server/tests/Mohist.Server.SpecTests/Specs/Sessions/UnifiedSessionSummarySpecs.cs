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
/// Issue-523 T-001 / design D2: enriched fields on the unified Session read
/// projection. <see cref="AgentSessionQuerier.GetUnifiedSessionSummaryAsync"/>
/// is the single shared read contract for the Web session detail page; it must
/// carry every fact the page consumes for both <c>agent-launch</c> and
/// <c>workflow</c> sources — current-turn and input/turn observations,
/// terminal/failure evidence, model/usage, recovery availability, and
/// runtime binding — while keeping source-specific fields absent for the
/// opposite source.
/// </summary>
public class UnifiedSessionSummarySpecs
{
    private const string ProjectA = "proj-summary-A";
    private const string AgentId = "agent_summary";
    private const string AgentName = "summary-agent";
    private const string AgentLaunchSession = "s_summary_agent";
    private const string WorkflowSession = "s_summary_workflow";
    private const string WorkflowRunId = "wr-summary-1";
    private const string WorkflowSessionName = "coder";
    private const int WorkflowIssueNumber = 100;
    private const string EnrichedActiveTurnId = "turn-active-1";
    private const string EnrichedActiveInputId = "input-active-1";
    private const string UnsupportedSourceSession = "s_summary_unsupported";

    private static readonly DateTime CreatedAt = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

    private static readonly ProjectInfo ProjectAInfo = new() { Id = ProjectA, Name = "summary-a" };

    [Fact]
    public async Task Show_AgentLaunchSession_CarriesEnrichedFieldsFromTranscriptAndState()
    {
        var db = await BuildEnrichedDbAsync(seedActiveAgent: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        Assert.Equal("gpt-4o", data.GetProperty("resolvedModel").GetString());
        Assert.Equal("rate_limited", data.GetProperty("failureCategory").GetString());
        Assert.Equal("OpenCode provider rate limit", data.GetProperty("failureReason").GetString());
        Assert.Equal(2, data.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(1, data.GetProperty("toolErrorCount").GetInt32());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
        Assert.Equal("rt-agent", data.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", data.GetProperty("runtime").GetString());
        Assert.True(data.TryGetProperty("usage", out _));
        Assert.Equal(1, data.GetProperty("inputs").GetArrayLength());
        Assert.Equal("accepted", data.GetProperty("inputs")[0].GetProperty("acceptance").GetString());
        Assert.Equal(1, data.GetProperty("turns").GetArrayLength());
        Assert.Equal(EnrichedActiveTurnId, data.GetProperty("turns")[0].GetProperty("id").GetString());
        Assert.Equal("executing", data.GetProperty("turns")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Show_WorkflowSession_CarriesEnrichedFieldsFromTranscriptAndState()
    {
        var db = await BuildEnrichedDbAsync(seedActiveWorkflow: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, WorkflowSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        Assert.Equal("claude-3", data.GetProperty("resolvedModel").GetString());
        Assert.Equal("context_exhaustion", data.GetProperty("failureCategory").GetString());
        Assert.Equal("Runtime context window exhausted", data.GetProperty("failureReason").GetString());
        Assert.Equal(3, data.GetProperty("toolCallCount").GetInt32());
        Assert.Equal(2, data.GetProperty("toolErrorCount").GetInt32());
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
        Assert.Equal("rt-workflow", data.GetProperty("runtimeSessionId").GetString());
        Assert.Equal("opencode", data.GetProperty("runtime").GetString());
        Assert.True(data.TryGetProperty("usage", out _));
        Assert.Equal(1, data.GetProperty("inputs").GetArrayLength());
        Assert.Equal(1, data.GetProperty("turns").GetArrayLength());
        Assert.Equal(EnrichedActiveTurnId, data.GetProperty("turns")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_RecoveryAvailableTrue_WhenActivityIdle()
    {
        var db = await BuildEnrichedDbAsync();
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        Assert.True(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("idle", data.GetProperty("activity").GetString());
        Assert.False(data.TryGetProperty("currentTurnId", out _));
    }

    [Fact]
    public async Task Show_AgentLaunchSession_RecoveryAvailableFalse_WhenActivityActive()
    {
        var db = await BuildEnrichedDbAsync(seedActiveAgent: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
    }

    [Fact]
    public async Task Show_WorkflowSession_RecoveryAvailableFalse_WhenActivityActive()
    {
        var db = await BuildEnrichedDbAsync(seedActiveWorkflow: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, WorkflowSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        Assert.False(data.GetProperty("recoveryAvailable").GetBoolean());
        Assert.Equal("active", data.GetProperty("activity").GetString());
        Assert.Equal(EnrichedActiveTurnId, data.GetProperty("currentTurnId").GetString());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_FailureEvidenceScopedToCurrentRuntimeBinding()
    {
        var db = await BuildEnrichedDbAsync(seedPriorRuntimeFailure: true);
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        Assert.Equal("rate_limited", data.GetProperty("failureCategory").GetString());
        Assert.Equal("OpenCode provider rate limit", data.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Show_AgentLaunchSession_CarriesUsageFromSession()
    {
        var db = await BuildEnrichedDbAsync();
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        var usage = data.GetProperty("usage");
        Assert.Equal(120, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(60, usage.GetProperty("outputTokens").GetInt64());
        Assert.Equal(180, usage.GetProperty("totalTokens").GetInt64());
        Assert.Equal(0.42, usage.GetProperty("costAmount").GetDouble());
    }

    [Fact]
    public async Task Show_UnsupportedSourceKind_Returns404()
    {
        var db = await BuildEnrichedDbAsync();
        await SeedUnsupportedSourceSessionAsync(db.Factory);
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, UnsupportedSourceSession, CreateQuerier(db), CancellationToken.None);
        await AssertNotFoundAsync(result, UnsupportedSourceSession);
    }

    [Fact]
    public async Task Transcript_UnsupportedSourceKind_Returns404()
    {
        var db = await BuildEnrichedDbAsync();
        await SeedUnsupportedSourceSessionAsync(db.Factory);
        var result = await UnifiedSessionRoutes.HandleTranscriptAsync(
            ProjectAInfo, UnsupportedSourceSession, runtimeSessionId: null, CreateQuerier(db), CancellationToken.None);
        await AssertNotFoundAsync(result, UnsupportedSourceSession);
    }

    [Fact]
    public async Task Show_AgentLaunchSession_OmitsNullableFailureFields_WhenTranscriptHasNoTerminalFact()
    {
        var db = await BuildBareDbAsync();
        var result = await UnifiedSessionRoutes.HandleShowAsync(ProjectAInfo, AgentLaunchSession, CreateQuerier(db), CancellationToken.None);
        var data = await OkDataAsync(result);
        var json = data.GetRawText();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("failureCategory", out _));
        Assert.False(doc.RootElement.TryGetProperty("failureReason", out _));
        Assert.False(doc.RootElement.TryGetProperty("toolCallCount", out _));
        Assert.False(doc.RootElement.TryGetProperty("toolErrorCount", out _));
        Assert.False(doc.RootElement.TryGetProperty("currentTurnId", out _));
        Assert.True(doc.RootElement.GetProperty("recoveryAvailable").GetBoolean());
    }

    private static AgentSessionQuerier CreateQuerier(SummaryTestDb db) =>
        new(db.Factory, new AgentSessionQuery(db.Factory, TimeProvider));

    private static async Task<SummaryTestDb> BuildEnrichedDbAsync(
        bool seedActiveAgent = false,
        bool seedActiveWorkflow = false,
        bool seedPriorRuntimeFailure = false)
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);

        await SeedAgentAsync(factory);
        await SeedEnrichedSessionAsync(factory, "agent-launch", AgentLaunchSession, "rt-agent",
            "gpt-4o", active: seedActiveAgent, recordedMinutes: 2, lastDataMinutes: 5);
        await SeedEnrichedSessionAsync(factory, "workflow", WorkflowSession, "rt-workflow",
            "claude-3", active: seedActiveWorkflow, recordedMinutes: 3, lastDataMinutes: 8);
        await SeedEnrichedTranscriptAsync(factory, AgentLaunchSession, "rt-agent",
            "gpt-4o", "rate_limited", "OpenCode provider rate limit", 2, 1, seedPriorRuntimeFailure);
        await SeedEnrichedTranscriptAsync(factory, WorkflowSession, "rt-workflow",
            "claude-3", "context_exhaustion", "Runtime context window exhausted", 3, 2, false);

        return new SummaryTestDb(database, factory);
    }

    private static async Task<SummaryTestDb> BuildBareDbAsync()
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        await SeedAgentAsync(factory);
        await SeedBareSessionAsync(factory, "agent-launch", AgentLaunchSession,
            "rt-agent", "gpt-4o", agentId: AgentId, agentName: AgentName);
        await SeedBareSessionAsync(factory, "workflow", WorkflowSession,
            "rt-" + WorkflowSession, "claude-3",
            workflowRunId: WorkflowRunId, sessionName: WorkflowSessionName, issueNumber: WorkflowIssueNumber);
        return new SummaryTestDb(database, factory);
    }

    private static async Task SeedAgentAsync(IDbContextFactory<MohistDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        var domain = new DomainAgent { Id = AgentId, ProjectId = ProjectA, Name = AgentName, Status = AgentStatus.Active };
        db.Agents.Add(new AgentRow
        {
            Id = AgentId, ProjectId = ProjectA, Name = AgentName, Status = AgentStatus.Active,
            State = AgentStore.Serialize(domain),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedBareSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sourceKind, string sessionId, string runtimeSessionId, string model,
        string? agentId = null, string? agentName = null,
        string? workflowRunId = null, string? sessionName = null, int? issueNumber = null)
    {
        var labels = BuildLabels(sourceKind, agentId, agentName, workflowRunId, sessionName, issueNumber);
        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = "runner-" + sessionId, workDir = (string?)null, runtime = "opencode" },
            settings = new { model },
            status = new
            {
                agentRuntimeSessionId = runtimeSessionId,
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
            AgentSessionId = runtimeSessionId,
            Status = "opened",
            CreatedAt = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedEnrichedSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sourceKind, string sessionId, string runtimeSessionId, string model,
        bool active = false, int recordedMinutes = 2, int lastDataMinutes = 5)
    {
        var agentId = sourceKind == "agent-launch" ? AgentId : null;
        var agentName = sourceKind == "agent-launch" ? AgentName : null;
        var workflowRunId = sourceKind == "workflow" ? WorkflowRunId : null;
        var sessionName = sourceKind == "workflow" ? WorkflowSessionName : null;
        var issueNumber = sourceKind == "workflow" ? (int?)WorkflowIssueNumber : null;
        var labels = BuildLabels(sourceKind, agentId, agentName, workflowRunId, sessionName, issueNumber);
        object[] turns = active ? new object[]
        {
            new
            {
                id = EnrichedActiveTurnId, sequence = 1L,
                inputIds = new[] { EnrichedActiveInputId },
                status = "executing", jobId = (string?)null,
                recordedAt = CreatedAt.AddMinutes(recordedMinutes),
                updatedAt = CreatedAt.AddMinutes(lastDataMinutes),
            },
        } : new object[0];
        object[] inputs = active ? new object[]
        {
            new
            {
                id = EnrichedActiveInputId, sequence = 1L,
                text = "follow-up text", source = sourceKind + "-source",
                acceptance = "accepted",
                recordedAt = CreatedAt.AddMinutes(recordedMinutes),
                jobId = (string?)null,
            },
        } : new object[0];

        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = "runner-" + sessionId, workDir = (string?)null, runtime = "opencode" },
            settings = new { model },
            status = new
            {
                agentRuntimeSessionId = runtimeSessionId,
                activity = active ? "active" : "idle",
                createdAt = CreatedAt,
                lastDataAt = CreatedAt.AddMinutes(lastDataMinutes),
                usageSummary = new
                {
                    inputTokens = 120L, outputTokens = 60L, totalTokens = 180L,
                    costAmount = 0.42d, costCurrency = "USD",
                },
                turns,
                inputs,
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
            CreatedAt = CreatedAt,
        });
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, string> BuildLabels(
        string sourceKind, string? agentId, string? agentName,
        string? workflowRunId, string? sessionName, int? issueNumber)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [AgentSessionQueryMetadataKeys.SourceKind] = sourceKind,
        };
        if (agentId is not null) labels[GenericAgentSessionMetadata.AgentId] = agentId;
        if (agentName is not null) labels[GenericAgentSessionMetadata.AgentName] = agentName;
        if (workflowRunId is not null) labels[AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId;
        if (sessionName is not null) labels[AgentSessionQueryMetadataKeys.SessionName] = sessionName;
        if (issueNumber is not null) labels[AgentSessionQueryMetadataKeys.IssueNumber] = issueNumber.Value.ToString();
        return labels;
    }

    private static async Task SeedEnrichedTranscriptAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId, string runtimeSessionId,
        string resolvedModel, string failureCategory, string failureReason,
        int totalTools, int errorTools, bool seedPriorRuntimeFailure)
    {
        await using var db = factory.CreateDbContext();

        var currentTurn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = runtimeSessionId,
            Sequence = 2,
            StartedAt = CreatedAt.AddMinutes(10),
            UpdatedAt = CreatedAt.AddMinutes(20),
        };
        db.AgentSessionTranscriptTurns.Add(currentTurn);

        long? priorTurnId = null;
        if (seedPriorRuntimeFailure)
        {
            var priorTurn = new AgentSessionTranscriptTurnRow
            {
                SessionId = sessionId,
                RuntimeSessionId = "rt-prior-" + sessionId,
                Sequence = 1,
                StartedAt = CreatedAt,
                UpdatedAt = CreatedAt.AddMinutes(8),
            };
            db.AgentSessionTranscriptTurns.Add(priorTurn);
            await db.SaveChangesAsync();
            priorTurnId = priorTurn.Id;
        }
        else
        {
            await db.SaveChangesAsync();
        }

        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            TurnId = currentTurn.Id,
            Sequence = 1,
            Type = TranscriptPartTypes.Model,
            CorrelationKey = "model",
            PayloadJson = JsonSerializer.Serialize(new { resolvedModel }, JSON.Options),
            LastSeenAt = CreatedAt.AddMinutes(11),
        });

        for (var i = 0; i < totalTools; i++)
        {
            var isError = i < errorTools;
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = currentTurn.Id,
                Sequence = 10 + i,
                Type = TranscriptPartTypes.Tool,
                CorrelationKey = $"call_{i}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    toolCallId = $"call_{i}",
                    toolName = $"tool_{i}",
                    status = isError ? "failed" : "completed",
                }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(12 + i),
            });
        }

        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            TurnId = currentTurn.Id,
            Sequence = 100,
            Type = TranscriptPartTypes.SessionActivity,
            CorrelationKey = "agent-job:current:terminal",
            PayloadJson = JsonSerializer.Serialize(new
            {
                status = "failed",
                failureReason = failureReason,
                failureCategory = failureCategory,
            }, JSON.Options),
            LastSeenAt = CreatedAt.AddMinutes(20),
        });

        if (seedPriorRuntimeFailure && priorTurnId is not null)
        {
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = priorTurnId.Value,
                Sequence = 50,
                Type = TranscriptPartTypes.SessionActivity,
                CorrelationKey = "agent-job:prior:terminal",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    status = "failed",
                    failureReason = "prior-reason-should-not-leak",
                    failureCategory = "prior-category-should-not-leak",
                }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(8),
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedUnsupportedSourceSessionAsync(IDbContextFactory<MohistDbContext> factory)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [AgentSessionQueryMetadataKeys.SourceKind] = "legacy-test-source",
        };
        var stateJson = JsonSerializer.Serialize(new
        {
            id = UnsupportedSourceSession,
            metadata = new { labels },
            runtime = new { runnerId = "runner-unsupported", workDir = (string?)null, runtime = "opencode" },
            settings = new { model = (string?)null },
            status = new
            {
                agentRuntimeSessionId = (string?)null,
                activity = "idle",
                createdAt = CreatedAt,
                lastDataAt = CreatedAt,
            },
        }, JSON.Options);

        await using var db = factory.CreateDbContext();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = UnsupportedSourceSession,
            State = stateJson,
            RunnerId = "runner-unsupported",
            AgentSessionId = null,
            Status = "opened",
            CreatedAt = CreatedAt,
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

    private sealed record SummaryTestDb(TestSqliteDatabase Database, IDbContextFactory<MohistDbContext> Factory) : IDisposable
    {
        public void Dispose() => Database.Dispose();
    }
}