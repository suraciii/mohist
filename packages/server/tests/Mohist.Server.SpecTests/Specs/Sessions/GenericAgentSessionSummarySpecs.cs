using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-130 T-003: focused unit specs for
/// <see cref="AgentSessionQuerier.GetGenericSessionSummaryAsync"/> that
/// exercise the enriched generic-session summary read model
/// (<see cref="GenericAgentSessionSummaryDto"/>), the absent-workflow-fields
/// invariant, and the not-found paths.
/// </summary>
public class GenericAgentSessionSummarySpecs
{
    private const string ProjectA = "proj-summary-A";
    private const string ProjectB = "proj-summary-B";

    private const string AgentId = "agent_s1";
    private const string AgentName = "agent-summary";
    private const string AgentIssueNumber = "42";
    private const string AgentEpicNumber = "7";
    private const string AgentRepository = "mohist/repo-s";
    private const string AgentWorkspacePath = "/work/s1";

    private const string SessionId = "s_summary_1";
    private const string SessionIdUnknown = "s_nonexistent";
    private const string SessionIdWorkflow = "s_workflow_1";

    private static readonly DateTime CreatedAt = new(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Summary_CarriesEnrichedFields()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: true);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Equal(SessionId, result!.SessionId);
        Assert.Equal(AgentId, result.AgentId);
        Assert.Equal(AgentName, result.AgentName);
        Assert.Equal("running", result.Status);
        Assert.True(result.RecoveryAvailable);
        Assert.Equal(CreatedAt.ToString("o"), result.CreatedAt);
        Assert.NotNull(result.LastActivityAt);
        Assert.Equal("gpt-4o", result.ResolvedModel);
        Assert.NotNull(result.Usage);
        Assert.Equal(3, result.ToolCallCount);
        Assert.Equal(1, result.ToolErrorCount);
    }

    [Fact]
    public async Task Summary_CarriesFailureCategory_WhenTranscriptHasClosedEvent()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: true, terminalStatus: "failed");
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Equal("rate_limited", result!.FailureCategory);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task Summary_ReportsRecoveryUnavailableForAnActiveTurn()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false, active: true);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Equal("running", result!.Status);
        Assert.False(result.RecoveryAvailable);
    }

    [Fact]
    public async Task Summary_ProjectsTranscriptEventsInSequenceOrder_WhenRowsWereInsertedOutOfOrder()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false);
        await SeedOutOfOrderTranscriptPartsAsync(fixture, SessionId);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Equal("sequence-last-model", result!.ResolvedModel);
        Assert.Equal("sequence-last-failure", result.FailureCategory);
    }

    [Fact]
    public async Task Summary_CarriesContextRefs_WhenPresent()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false, withContextRefs: true);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.NotNull(result!.ContextRefs);
        Assert.Equal(int.Parse(AgentIssueNumber), result.ContextRefs!.IssueNumber);
        Assert.Equal(int.Parse(AgentEpicNumber), result.ContextRefs.EpicNumber);
        Assert.Equal(AgentRepository, result.ContextRefs.Repository);
        Assert.Equal(AgentWorkspacePath, result.ContextRefs.WorkspacePath);
    }

    [Fact]
    public async Task Summary_ContextRefsIsNull_WhenNoContextReferences()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false, withContextRefs: false);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Null(result!.ContextRefs);
    }

    [Fact]
    public async Task Summary_NoWorkflowFields()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        // The DTO is a sealed record — verify it does not have workflow-only
        // properties by checking the JSON serialization omits them.
        var json = JsonSerializer.Serialize(result, JSON.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("workflowRunId", out _));
        Assert.False(doc.RootElement.TryGetProperty("sessionName", out _));
        Assert.False(doc.RootElement.TryGetProperty("workId", out _));
        Assert.False(doc.RootElement.TryGetProperty("workType", out _));
        Assert.False(doc.RootElement.TryGetProperty("stage", out _));
    }

    [Fact]
    public async Task Summary_UnknownSessionId_ReturnsNull()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionIdUnknown);

        Assert.Null(result);
    }

    [Fact]
    public async Task Summary_DifferentProject_ReturnsNull()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectB, SessionId);

        Assert.Null(result);
    }

    [Fact]
    public async Task Summary_WorkflowSession_ReturnsNull()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedWorkflowSessionAsync(fixture);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionIdWorkflow);

        Assert.Null(result);
    }

    private static AgentSessionQuerier CreateQuerier(IDbContextFactory<MohistDbContext> factory)
    {
        var sessionQuery = new AgentSessionQuery(factory, TimeProvider);
        return new AgentSessionQuerier(factory, sessionQuery, TimeProvider);
    }

    private static async Task SeedGenericSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        bool hasTranscript,
        bool withContextRefs = false,
        string? terminalStatus = null,
        bool active = false)
    {
        await using var db = factory.CreateDbContext();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = AgentId,
            [GenericAgentSessionMetadata.AgentName] = AgentName,
        };

        if (withContextRefs)
        {
            labels[GenericAgentSessionMetadata.IssueNumber] = AgentIssueNumber;
            labels[GenericAgentSessionMetadata.EpicNumber] = AgentEpicNumber;
            labels[GenericAgentSessionMetadata.Repository] = AgentRepository;
            labels[GenericAgentSessionMetadata.WorkspacePath] = AgentWorkspacePath;
        }

        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = $"runner-{sessionId}", workDir = (string?)null, runtime = "opencode" },
            settings = new { model = "gpt-4o" },
            status = new
            {
                agentRuntimeSessionId = active || terminalStatus is not null ? "runtime-" + sessionId : null,
                createdAt = CreatedAt,
                lastDataAt = active || terminalStatus is not null ? TimeProvider.GetUtcNow().UtcDateTime : CreatedAt.AddMinutes(5),
            },
        }, JSON.Options);

        var row = new AgentSessionRow
        {
            Id = sessionId,
            State = stateJson,
            RunnerId = $"runner-{sessionId}",
            AgentSessionId = "session-" + sessionId,
            Status = "opened",
            CreatedAt = CreatedAt,
        };
        db.AgentSessions.Add(row);

        if (hasTranscript)
        {
            var turn = new AgentSessionTranscriptTurnRow
            {
                SessionId = sessionId,
                RuntimeSessionId = "runtime-" + sessionId,
                Sequence = 1,
                StartedAt = CreatedAt,
                UpdatedAt = CreatedAt.AddMinutes(5),
            };
            db.AgentSessionTranscriptTurns.Add(turn);
            await db.SaveChangesAsync();

            // Model event
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 1,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "model",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    resolvedModel = "gpt-4o",
                }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(1),
            });

            // Tool call events (3 calls, 1 error)
            for (var i = 0; i < 3; i++)
            {
                db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = (long)10 + i,
                    Type = TranscriptPartTypes.Tool,
                    CorrelationKey = $"call_{i}",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        toolCallId = $"call_{i}",
                        toolName = $"tool_{i}",
                        status = i == 2 ? "failed" : "completed",
                    }, JSON.Options),
                    LastSeenAt = CreatedAt.AddMinutes(2 + i),
                });
            }

            // Session closed event (for terminal status)
            if (terminalStatus is not null)
            {
                db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = 100,
                    Type = TranscriptPartTypes.SessionClosed,
                    CorrelationKey = "session.closed",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        status = terminalStatus,
                        failureCategory = terminalStatus == "failed" ? "rate_limited" : null,
                    }, JSON.Options),
                    LastSeenAt = CreatedAt.AddMinutes(10),
                });
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedOutOfOrderTranscriptPartsAsync(IDbContextFactory<MohistDbContext> factory, string sessionId)
    {
        await using var db = factory.CreateDbContext();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = "runtime-" + sessionId,
            Sequence = 1,
            StartedAt = CreatedAt,
            UpdatedAt = CreatedAt.AddMinutes(5),
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.AddRange(
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 20,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "model-latest-by-sequence",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "sequence-last-model" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(20),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 10,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "model-inserted-last",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "inserted-last-model" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(10),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 30,
                Type = TranscriptPartTypes.SessionClosed,
                CorrelationKey = "closed-latest-by-sequence",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = "sequence-last-failure" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(30),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 15,
                Type = TranscriptPartTypes.SessionClosed,
                CorrelationKey = "closed-inserted-last",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = "inserted-last-failure" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(15),
            });
        await db.SaveChangesAsync();
    }

    private static async Task SeedWorkflowSessionAsync(IDbContextFactory<MohistDbContext> factory)
    {
        await using var db = factory.CreateDbContext();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = "wr-w1",
            [AgentSessionQueryMetadataKeys.SessionName] = "session-w1",
            [AgentSessionQueryMetadataKeys.IssueNumber] = "100",
        };

        var stateJson = JsonSerializer.Serialize(new
        {
            id = SessionIdWorkflow,
            metadata = new { labels },
            runtime = new { runnerId = $"runner-{SessionIdWorkflow}", workDir = (string?)null },
            settings = new { },
            status = new { createdAt = CreatedAt },
        }, JSON.Options);

        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = SessionIdWorkflow,
            State = stateJson,
            RunnerId = $"runner-{SessionIdWorkflow}",
            AgentSessionId = "session-" + SessionIdWorkflow,
            Status = "opened",
            CreatedAt = CreatedAt,
        });

        await db.SaveChangesAsync();
    }
}
