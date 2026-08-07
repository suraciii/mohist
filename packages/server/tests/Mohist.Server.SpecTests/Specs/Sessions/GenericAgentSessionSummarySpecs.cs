using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
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
    private const string AgentWorkspaceName = "pay";

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
        Assert.Equal("idle", result.Activity);
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
        Assert.Null(result!.FailureCategory);
        Assert.Equal("idle", result.Activity);
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
        Assert.Equal("active", result!.Activity);
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
        Assert.Null(result.FailureCategory);
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
        Assert.Equal(AgentWorkspaceName, result.ContextRefs.WorkspaceName);
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

    [Fact]
    public async Task Summary_CarriesFailureReason_FromSameLatestTerminalFact_AsFailureCategory()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(
            fixture,
            SessionId,
            hasTranscript: true,
            terminalStatus: "failed",
            failureReason: "AgentJob requires 'workspace.path' in dispatch variables");
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Equal("idle", result!.Activity);
        Assert.Null(result.FailureCategory);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Summary_OmitsFailureReasonAndCategory_OnSuccessfulSession()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(
            fixture,
            SessionId,
            hasTranscript: true,
            terminalStatus: "completed",
            failureReason: null);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Equal("idle", result!.Activity);
        Assert.Null(result.FailureReason);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Summary_LatestTerminalFact_AcrossMultipleTurns_PicksNewestTurn()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false);
        await SeedMultiTurnClosedFactsAsync(fixture, SessionId);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        // The newer Runtime Session's turn (sequence 2) restarts part
        // sequences at 1 but the AgentJob-owned close on turn 2 is
        // authoritative; the older turn-1 close is ignored.
        Assert.Null(result!.FailureReason);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Summary_LatestTerminalFact_OnSameTurn_PicksLatestPartSequence()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false);
        await SeedMultipleClosedPartsSameTurnAsync(fixture, SessionId);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        Assert.Null(result!.FailureReason);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Summary_OmitsNullableFailureFields_InJsonSerialization()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(fixture, SessionId, hasTranscript: false);
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result, JSON.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("failureReason", out _),
            "Successful session omits nullable failureReason from the wire");
        Assert.False(doc.RootElement.TryGetProperty("failureCategory", out _),
            "Successful session omits nullable failureCategory from the wire");
    }

    [Fact]
    public async Task Summary_IncludesFailureReasonAndCategory_AsSeparateFields_WhenPresent()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await SeedGenericSessionAsync(
            fixture,
            SessionId,
            hasTranscript: true,
            terminalStatus: "failed",
            failureReason: "AgentJob requires 'workspace.path' in dispatch variables");
        var querier = CreateQuerier(fixture);

        var result = await querier.GetGenericSessionSummaryAsync(ProjectA, SessionId);

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result, JSON.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("failureReason", out _));
        Assert.False(doc.RootElement.TryGetProperty("failureCategory", out _));
    }

    private static AgentSessionQuerier CreateQuerier(IDbContextFactory<MohistDbContext> factory)
    {
        var sessionQuery = new AgentSessionQuery(factory, TimeProvider);
        return new AgentSessionQuerier(factory, sessionQuery);
    }

    private static async Task SeedGenericSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        bool hasTranscript,
        bool withContextRefs = false,
        string? terminalStatus = null,
        bool active = false,
        string? failureReason = null)
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
            labels[GenericAgentSessionMetadata.WorkspaceName] = AgentWorkspaceName;
        }

        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = $"runner-{sessionId}", workDir = (string?)null, runtime = "opencode" },
            settings = new { model = "gpt-4o" },
            status = new
            {
                agentRuntimeSessionId = active ? "runtime-" + sessionId : null,
                activity = active ? "active" : "idle",
                createdAt = CreatedAt,
                lastDataAt = active ? TimeProvider.GetUtcNow().UtcDateTime : CreatedAt.AddMinutes(5),
            },
        }, JSON.Options);

        var row = new AgentSessionRow
        {
            Id = sessionId,
            State = stateJson,
            RunnerId = $"runner-{sessionId}",
            AgentSessionId = "runtime-" + sessionId,
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
                    Type = "session.closed",
                    CorrelationKey = "session.closed",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        status = terminalStatus,
                        failureReason = failureReason,
                        failureCategory = terminalStatus == "failed" ? "rate_limited" : null,
                    }, JSON.Options),
                    LastSeenAt = CreatedAt.AddMinutes(10),
                });
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedMultiTurnClosedFactsAsync(IDbContextFactory<MohistDbContext> factory, string sessionId)
    {
        await using var db = factory.CreateDbContext();

        // Two Runtime Sessions (turns): turn 1 has the older failure
        // context, turn 2 carries the AgentJob-owned close on the
        // current runtime. The session's current runtime session id is
        // runtime-2 so the current-runtime filter narrows the candidate
        // set to the turn-2 close.
        var turn1 = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = "runtime-1",
            Sequence = 1,
            StartedAt = CreatedAt,
            UpdatedAt = CreatedAt.AddMinutes(5),
        };
        var turn2 = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = "runtime-2",
            Sequence = 2,
            StartedAt = CreatedAt.AddMinutes(10),
            UpdatedAt = CreatedAt.AddMinutes(15),
        };
        db.AgentSessionTranscriptTurns.AddRange(turn1, turn2);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.AddRange(
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn1.Id,
                Sequence = 50,
                Type = "session.closed",
                CorrelationKey = "agent-job:old:terminal",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    status = "failed",
                    failureReason = "older-turn-reason",
                    failureCategory = "older-turn-category",
                }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(7),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn2.Id,
                Sequence = 1,
                Type = "session.closed",
                CorrelationKey = "agent-job:newest:terminal",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    status = "failed",
                    failureReason = "newest-run-reason",
                    failureCategory = "newest-run-category",
                }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(15),
            });
        await db.SaveChangesAsync();

        // Point the session at the current runtime so the
        // current-runtime filter selects turn 2's close.
        var row = await db.AgentSessions.FindAsync(sessionId);
        Assert.NotNull(row);
        row!.AgentSessionId = "runtime-2";
        await db.SaveChangesAsync();
    }

    private static async Task SeedMultipleClosedPartsSameTurnAsync(IDbContextFactory<MohistDbContext> factory, string sessionId)
    {
        await using var db = factory.CreateDbContext();

        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            RuntimeSessionId = "runtime-current",
            Sequence = 1,
            StartedAt = CreatedAt,
            UpdatedAt = CreatedAt.AddMinutes(15),
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.AddRange(
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 5,
                Type = "session.closed",
                CorrelationKey = "agent-job:earlier:terminal",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    status = "failed",
                    failureReason = "earlier-part-reason",
                    failureCategory = "earlier-part-category",
                }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(5),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 20,
                Type = "session.closed",
                CorrelationKey = "agent-job:latest:terminal",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    status = "failed",
                    failureReason = "latest-part-reason",
                    failureCategory = "latest-part-category",
                }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(15),
            });
        await db.SaveChangesAsync();

        var row = await db.AgentSessions.FindAsync(sessionId);
        Assert.NotNull(row);
        row!.AgentSessionId = "runtime-current";
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
                Type = "session.closed",
                CorrelationKey = "closed-latest-by-sequence",
                PayloadJson = JsonSerializer.Serialize(new { status = "failed", failureCategory = "sequence-last-failure" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(30),
            },
            new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 15,
                Type = "session.closed",
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
            AgentSessionId = "runtime-" + SessionIdWorkflow,
            Status = "opened",
            CreatedAt = CreatedAt,
        });

        await db.SaveChangesAsync();
    }
}
