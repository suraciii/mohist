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

internal static class UnifiedSessionSummaryFactory
{
    public const string ProjectA = "proj-summary-A";
    public const string AgentId = "agent_summary";
    public const string AgentName = "summary-agent";
    public const string AgentLaunchSession = "s_summary_agent";
    public const string WorkflowSession = "s_summary_workflow";
    public const string WorkflowRunId = "wr-summary-1";
    public const string WorkflowSessionName = "coder";
    public const int WorkflowIssueNumber = 100;
    public const string EnrichedActiveTurnId = "turn-active-1";
    public const string EnrichedActiveInputId = "input-active-1";
    public const string EnrichedQueuedTurnId = "turn-queued-1";
    public const string UnsupportedSourceSession = "s_summary_unsupported";

    public static readonly DateTime CreatedAt = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    public static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

    public static readonly ProjectInfo ProjectAInfo = new() { Id = ProjectA, Name = "summary-a" };

    public static AgentSessionQuerier CreateQuerier(SummaryTestDb db) =>
        new(db.Factory, new AgentSessionQuery(db.Factory, TimeProvider));

    public static async Task<SummaryTestDb> BuildEnrichedDbAsync(
        bool seedActiveAgent = false,
        bool seedActiveWorkflow = false,
        bool seedPriorRuntimeFailure = false,
        bool seedQueuedTurn = false,
        bool seedQueuedIdleTurn = false,
        bool seedTurnResult = false,
        bool seedRecoveryHistory = false,
        bool seedCompactionEventOnly = false,
        string? agentRuntimeSessionId = "rt-agent")
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);

        await SeedAgentAsync(factory);
        await SeedEnrichedSessionAsync(factory, "agent-launch", AgentLaunchSession, agentRuntimeSessionId,
            "gpt-4o", active: seedActiveAgent, recordedMinutes: 2, lastDataMinutes: 5,
            seedQueuedTurn: seedQueuedTurn, seedQueuedIdleTurn: seedQueuedIdleTurn, seedTurnResult: seedTurnResult);
        await SeedEnrichedSessionAsync(factory, "workflow", WorkflowSession, "rt-workflow",
            "claude-3", active: seedActiveWorkflow, recordedMinutes: 3, lastDataMinutes: 8);
        await SeedEnrichedTranscriptAsync(factory, AgentLaunchSession, "rt-agent",
            "gpt-4o", "rate_limited", "OpenCode provider rate limit", 2, 1, seedPriorRuntimeFailure,
            seedRecoveryHistory, seedCompactionEventOnly);
        await SeedEnrichedTranscriptAsync(factory, WorkflowSession, "rt-workflow",
            "claude-3", "context_exhaustion", "Runtime context window exhausted", 3, 2, false);

        return new SummaryTestDb(database, factory);
    }

    public static async Task<SummaryTestDb> BuildBareDbAsync()
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

    public static async Task SeedAgentAsync(IDbContextFactory<MohistDbContext> factory)
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

    public static async Task SeedBareSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sourceKind, string sessionId, string? runtimeSessionId, string model,
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

    public static async Task SeedEnrichedSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sourceKind, string sessionId, string? runtimeSessionId, string model,
        bool active = false, int recordedMinutes = 2, int lastDataMinutes = 5,
        bool seedQueuedTurn = false, bool seedQueuedIdleTurn = false, bool seedTurnResult = false)
    {
        var agentId = sourceKind == "agent-launch" ? AgentId : null;
        var agentName = sourceKind == "agent-launch" ? AgentName : null;
        var workflowRunId = sourceKind == "workflow" ? WorkflowRunId : null;
        var sessionName = sourceKind == "workflow" ? WorkflowSessionName : null;
        var issueNumber = sourceKind == "workflow" ? (int?)WorkflowIssueNumber : null;
        var labels = BuildLabels(sourceKind, agentId, agentName, workflowRunId, sessionName, issueNumber);
        var turns = new List<object>();
        if (seedTurnResult)
        {
            turns.Add(new
            {
                id = EnrichedActiveTurnId, sequence = 1L,
                inputIds = new[] { EnrichedActiveInputId },
                status = "completed", jobId = (string?)null,
                result = new { message = "initial launch completed", output = "artifact output", exitCode = 0 },
                recordedAt = CreatedAt.AddMinutes(recordedMinutes),
                updatedAt = CreatedAt.AddMinutes(lastDataMinutes),
            });
        }
        else if (active)
        {
            turns.Add(new
            {
                id = EnrichedActiveTurnId, sequence = 1L,
                inputIds = new[] { EnrichedActiveInputId },
                status = "executing", jobId = (string?)null,
                recordedAt = CreatedAt.AddMinutes(recordedMinutes),
                updatedAt = CreatedAt.AddMinutes(lastDataMinutes),
            });
        }
        if (seedQueuedTurn || seedQueuedIdleTurn)
        {
            turns.Add(new
            {
                id = EnrichedQueuedTurnId, sequence = 2L,
                inputIds = Array.Empty<string>(),
                status = "queued", jobId = (string?)null,
                recordedAt = CreatedAt.AddMinutes(recordedMinutes + 1),
                updatedAt = CreatedAt.AddMinutes(lastDataMinutes + 1),
            });
        }
        var inputs = turns.Count == 0 ? new List<object>() :
        [
            new
            {
                id = EnrichedActiveInputId, sequence = 1L,
                text = "follow-up text", source = sourceKind + "-source",
                acceptance = "accepted",
                recordedAt = CreatedAt.AddMinutes(recordedMinutes),
                jobId = (string?)null,
            },
        ];

        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = "runner-" + sessionId, workDir = (string?)null, runtime = "opencode" },
            settings = new { model },
            status = new
            {
                agentRuntimeSessionId = runtimeSessionId,
                activity = active && !seedTurnResult ? "active" : "idle",
                createdAt = CreatedAt,
                lastDataAt = CreatedAt.AddMinutes(lastDataMinutes),
                usageSummary = new
                {
                    inputTokens = 120L, outputTokens = 60L, totalTokens = 180L,
                    costAmount = 0.42d, costCurrency = "USD",
                },
                turns = turns.ToArray(),
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

    public static Dictionary<string, string> BuildLabels(
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

    public static async Task SeedEnrichedTranscriptAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId, string runtimeSessionId,
        string resolvedModel, string failureCategory, string failureReason,
        int totalTools, int errorTools, bool seedPriorRuntimeFailure,
        bool seedRecoveryHistory = false, bool seedCompactionEventOnly = false)
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

        if (seedRecoveryHistory)
        {
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = currentTurn.Id,
                Sequence = 101,
                Type = TranscriptPartTypes.SessionContextReset,
                CorrelationKey = "recovery-reset",
                PayloadJson = JsonSerializer.Serialize(new { reason = "reset", observedAt = CreatedAt.AddMinutes(21) }, JSON.Options),
                FirstSeenAt = CreatedAt.AddMinutes(21),
                LastSeenAt = CreatedAt.AddMinutes(21),
            });
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = currentTurn.Id,
                Sequence = 102,
                Type = TranscriptPartTypes.Compaction,
                CorrelationKey = "recovery-compaction",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    strategy = "summary",
                    summary = "Earlier context retained",
                    contextWindowUsedBefore = 900L,
                    contextWindowUsedAfter = 300L,
                    contextWindowSize = 1000L,
                    recordedAt = CreatedAt.AddMinutes(22),
                }, JSON.Options),
                FirstSeenAt = CreatedAt.AddMinutes(22),
                LastSeenAt = CreatedAt.AddMinutes(22),
            });
        }

        if (seedCompactionEventOnly)
        {
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = currentTurn.Id,
                Sequence = 101,
                Type = RuntimeEventTypes.CompactionEvent,
                CorrelationKey = "recovery-compaction-event",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    strategy = "summary",
                    summary = "Earlier context retained",
                    contextWindowUsedBefore = 900L,
                    contextWindowUsedAfter = 300L,
                    contextWindowSize = 1000L,
                    recordedAt = CreatedAt.AddMinutes(22),
                }, JSON.Options),
                FirstSeenAt = CreatedAt.AddMinutes(22),
                LastSeenAt = CreatedAt.AddMinutes(22),
            });
        }

        await db.SaveChangesAsync();
    }

    public static async Task SeedUnsupportedSourceSessionAsync(IDbContextFactory<MohistDbContext> factory)
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

    public static async Task<JsonElement> OkDataAsync(IResult result)
    {
        var (body, status) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        return body.GetProperty("data");
    }

    public static async Task AssertNotFoundAsync(IResult result, string expectedFragment)
    {
        var (body, status) = await ExecuteAsync(result);
        Assert.Equal(404, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Contains(expectedFragment, body.GetProperty("error").GetString()!);
    }

    public static async Task<(JsonElement body, int status)> ExecuteAsync(IResult result)
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
}

internal sealed record SummaryTestDb(TestSqliteDatabase Database, IDbContextFactory<MohistDbContext> Factory) : IDisposable
{
    public void Dispose() => Database.Dispose();
}
