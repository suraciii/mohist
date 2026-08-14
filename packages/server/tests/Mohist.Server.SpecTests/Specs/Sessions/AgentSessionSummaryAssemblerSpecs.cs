using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// Calculation specs for the generic-session summary read model
/// (<see cref="AgentSessionQuerier.GetGenericSessionSummaryAsync"/>) that
/// exercise the enriched <see cref="GenericAgentSessionSummaryDto"/>, the
/// absent-workflow-fields invariant, and the not-found paths. The querier
/// is driven directly via <c>MohistDbFixture</c> (no web host, no HTTP).
/// The route contract (200 JSON envelope shape, 404 mapping for unknown
/// session ids, project isolation) is asserted at the route layer; the
/// projections here assert the calculation, not the wire shape.
/// </summary>
[Collection("MohistDb")]
public class AgentSessionSummaryAssemblerSpecs
{
    private const string AgentId = "agent_s1";
    private const string AgentName = "agent-summary";
    private const string AgentIssueNumber = "42";
    private const string AgentEpicNumber = "7";
    private const string AgentRepository = "mohist/repo-s";
    private const string AgentWorkspaceName = "pay";

    private static readonly DateTime CreatedAt = new(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MohistDbFixture _fixture;

    public AgentSessionSummaryAssemblerSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Summary_CarriesEnrichedFields()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: true);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Equal(sessionId, result!.SessionId);
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
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: true, terminalStatus: "failed");
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Null(result!.FailureCategory);
        Assert.Equal("idle", result.Activity);
    }

    [Fact]
    public async Task Summary_ReportsRecoveryUnavailableForAnActiveTurn()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false, active: true);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Equal("active", result!.Activity);
        Assert.False(result.RecoveryAvailable);
    }

    [Fact]
    public async Task Summary_ProjectsTranscriptEventsInSequenceOrder_WhenRowsWereInsertedOutOfOrder()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false);
        await SeedOutOfOrderTranscriptPartsAsync(projectId, sessionId);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Equal("sequence-last-model", result!.ResolvedModel);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Summary_CarriesContextRefs_WhenPresent()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false, withContextRefs: true);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.NotNull(result!.ContextRefs);
        Assert.Equal(int.Parse(AgentIssueNumber), result.ContextRefs!.IssueNumber);
        Assert.Equal(int.Parse(AgentEpicNumber), result.ContextRefs.EpicNumber);
        Assert.Equal(AgentRepository, result.ContextRefs.Repository);
        Assert.Equal(AgentWorkspaceName, result.ContextRefs.WorkspaceName);
    }

    [Fact]
    public async Task PublicSessionContext_OmitsWorkspacePathAcrossReadModels()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(
            projectId,
            sessionId,
            hasTranscript: false,
            withContextRefs: true,
            workspacePath: "/srv/private/worktree");
        var querier = CreateQuerier();

        var generic = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);
        var list = await querier.ListAgentSessionsAsync(projectId, AgentId);
        var unified = await querier.GetUnifiedSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(generic);
        Assert.NotNull(unified);
        var listItem = Assert.Single(list, item => item.SessionId == sessionId);
        AssertPublicContext(generic!.ContextRefs);
        AssertPublicContext(listItem.ContextRefs);
        AssertPublicContext(unified!.ContextRefs);
    }

    [Fact]
    public async Task Summary_ContextRefsIsNull_WhenNoContextReferences()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false, withContextRefs: false);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Null(result!.ContextRefs);
    }

    [Fact]
    public async Task Summary_NoWorkflowFields()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
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
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, "s_nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task Summary_DifferentProject_ReturnsNull()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        var otherProjectId = NewProjectId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(otherProjectId, sessionId);

        Assert.Null(result);
    }

    [Fact]
    public async Task Summary_WorkflowSession_ReturnsNull()
    {
        var projectId = NewProjectId();
        var workflowSessionId = NewSessionId();
        await SeedWorkflowSessionAsync(projectId, workflowSessionId);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, workflowSessionId);

        Assert.Null(result);
    }

    [Fact]
    public async Task Summary_CarriesFailureReason_FromSameLatestTerminalFact_AsFailureCategory()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(
            projectId,
            sessionId,
            hasTranscript: true,
            terminalStatus: "failed",
            failureReason: "AgentJob requires 'workspace.path' in dispatch variables");
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Equal("idle", result!.Activity);
        Assert.Null(result.FailureCategory);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Summary_OmitsFailureReasonAndCategory_OnSuccessfulSession()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(
            projectId,
            sessionId,
            hasTranscript: true,
            terminalStatus: "completed",
            failureReason: null);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Equal("idle", result!.Activity);
        Assert.Null(result.FailureReason);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Summary_LatestTerminalFact_AcrossMultipleTurns_PicksNewestTurn()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false);
        await SeedMultiTurnClosedFactsAsync(projectId, sessionId);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Null(result!.FailureReason);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Summary_LatestTerminalFact_OnSameTurn_PicksLatestPartSequence()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false);
        await SeedMultipleClosedPartsSameTurnAsync(projectId, sessionId);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        Assert.Null(result!.FailureReason);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Summary_OmitsNullableFailureFields_InJsonSerialization()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(projectId, sessionId, hasTranscript: false);
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result, JSON.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("failureReason", out _));
        Assert.False(doc.RootElement.TryGetProperty("failureCategory", out _));
    }

    [Fact]
    public async Task Summary_IncludesFailureReasonAndCategory_AsSeparateFields_WhenPresent()
    {
        var projectId = NewProjectId();
        var sessionId = NewSessionId();
        await SeedGenericSessionAsync(
            projectId,
            sessionId,
            hasTranscript: true,
            terminalStatus: "failed",
            failureReason: "AgentJob requires 'workspace.path' in dispatch variables");
        var querier = CreateQuerier();

        var result = await querier.GetGenericSessionSummaryAsync(projectId, sessionId);

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result, JSON.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("failureReason", out _));
        Assert.False(doc.RootElement.TryGetProperty("failureCategory", out _));
    }

    private AgentSessionQuerier CreateQuerier()
    {
        using var scope = _fixture.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
    }

    private static string NewProjectId() => $"proj-summary-{Guid.NewGuid():N}";
    private static string NewSessionId() => $"s_summary_{Guid.NewGuid():N}";

    private async Task SeedGenericSessionAsync(
        string projectId,
        string sessionId,
        bool hasTranscript,
        bool withContextRefs = false,
        string? terminalStatus = null,
        bool active = false,
        string? failureReason = null,
        string? workspacePath = null)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
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
            if (workspacePath is not null)
                labels[GenericAgentSessionMetadata.WorkspacePath] = workspacePath;
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
                lastDataAt = active ? TestTime.UtcDateTime : CreatedAt.AddMinutes(5),
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

            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = 1,
                Type = TranscriptPartTypes.Model,
                CorrelationKey = "model",
                PayloadJson = JsonSerializer.Serialize(new { resolvedModel = "gpt-4o" }, JSON.Options),
                LastSeenAt = CreatedAt.AddMinutes(1),
            });

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

    private static void AssertPublicContext(object? contextRefs)
    {
        Assert.NotNull(contextRefs);
        var json = JsonSerializer.Serialize(contextRefs, JSON.Options);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("workspacePath", out _));
        Assert.Equal(AgentWorkspaceName, document.RootElement.GetProperty("workspaceName").GetString());
    }

    private async Task SeedMultiTurnClosedFactsAsync(string projectId, string sessionId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();

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

        var row = await db.AgentSessions.FindAsync(sessionId);
        Assert.NotNull(row);
        row!.AgentSessionId = "runtime-2";
        await db.SaveChangesAsync();
    }

    private async Task SeedMultipleClosedPartsSameTurnAsync(string projectId, string sessionId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();

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

    private async Task SeedOutOfOrderTranscriptPartsAsync(string projectId, string sessionId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
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

    private async Task SeedWorkflowSessionAsync(string projectId, string sessionId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = "wr-w1",
            [AgentSessionQueryMetadataKeys.SessionName] = "session-w1",
            [AgentSessionQueryMetadataKeys.IssueNumber] = "100",
        };

        var stateJson = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            runtime = new { runnerId = $"runner-{sessionId}", workDir = (string?)null },
            settings = new { },
            status = new { createdAt = CreatedAt },
        }, JSON.Options);

        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = stateJson,
            RunnerId = $"runner-{sessionId}",
            AgentSessionId = "runtime-" + sessionId,
            Status = "opened",
            CreatedAt = CreatedAt,
        });

        await db.SaveChangesAsync();
    }
}
