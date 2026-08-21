using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Fixture-agnostic seeding for the project event feed: writes the same
/// cross-aggregate rows (<c>IssueEvents</c>, <c>WorkflowRunEvents</c>,
/// <c>AgentSessionEvents</c>, persisted session lifecycle facts) that the
/// <c>ProjectEventFeedAssembler</c> reads, against any
/// <see cref="IServiceProvider"/> that carries the production service graph.
/// Shared by the HTTP-layer <c>ProjectEventsApiTestSupport</c> and the
/// assembler-level <c>ProjectEventFeedAssemblerTests</c> so the two layers
/// cannot drift on how events are seeded.
/// </summary>
internal sealed class ProjectEventSeedSupport
{
    public static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IServiceProvider _services;

    public ProjectEventSeedSupport(IServiceProvider services)
    {
        _services = services;
    }

    public async Task SeedIssueAsync(string projectId, int number)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Issue #{number}",
            Status = Mohist.Server.Issue.Domain.IssueStatus.InProgress,
        };
        await using var db = await CreateDbAsync();
        db.Issues.Add(new Mohist.Server.Infrastructure.Data.Issue.IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = Mohist.Server.Infrastructure.Data.Issue.IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    public async Task SeedWorkflowRunAsync(string projectId, string workflowRunId, int issueNumber)
    {
        await using var db = await CreateDbAsync();
        var state = JsonSerializer.Serialize(new
        {
            id = workflowRunId,
            metadata = new
            {
                createdAt = FixedTime,
                name = "test",
                projectId,
                issueNumber,
            },
            status = "Running",
            currentStageId = "build",
            stages = Array.Empty<object>(),
        });
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = state,
        });
        await db.SaveChangesAsync();
    }

    public async Task SeedAgentSessionAsync(string projectId, string sessionId)
    {
        await using var db = await CreateDbAsync();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
            [AgentSessionQueryMetadataKeys.IssueNumber] = "1",
            [AgentSessionQueryMetadataKeys.EpicNumber] = "7",
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = "wf-1",
        };
        var state = JsonSerializer.Serialize(new
        {
            id = sessionId,
            metadata = new { labels },
            settings = new { model = "gpt-4o" },
            status = new { createdAt = FixedTime },
        }, Mohist.Server.Infrastructure.JSON.Options);
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = state,
            Status = "opened",
            CreatedAt = FixedTime.UtcDateTime,
            RunnerId = "runner-1",
        });
        await db.SaveChangesAsync();
    }

    public async Task AppendSessionActivityFactAsync(
        string sessionId,
        DateTimeOffset time,
        string status = "failed",
        string? failureReason = "runner timeout")
    {
        await using var db = await CreateDbAsync();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = time.UtcDateTime,
            UpdatedAt = time.UtcDateTime,
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            TurnId = turn.Id,
            Sequence = 1,
            Type = TranscriptPartTypes.SessionActivity,
            CorrelationKey = $"session.activity_{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                activity = "idle",
                status,
                failureReason,
                exitCode = status == "completed" ? 0 : 1,
            }, Mohist.Server.Infrastructure.JSON.Options),
            FirstSeenAt = time.UtcDateTime,
            LastSeenAt = time.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    public async Task SeedEpicAsync(string projectId, int number)
    {
        await using var db = await CreateDbAsync();
        db.Epics.Add(new Mohist.Server.Infrastructure.Data.Epic.EpicRow
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Epic #{number}",
            Description = "",
            Priority = "p2",
            Status = "active",
            CreatedAt = FixedTime,
            UpdatedAt = FixedTime,
        });
        await db.SaveChangesAsync();
    }

    public async Task AppendIssueEventAsync(
        string projectId,
        int issueNumber,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            IssueEventPersistence.IssueSource(projectId, issueNumber),
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["issue"] = issueNumber.ToString(),
            });
    }

    public async Task AppendWorkflowEventAsync(
        string workflowRunId,
        string projectId,
        int issueNumber,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null,
        int? envelopeIssueNumber = null)
    {
        await AppendEventAsync(
            WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["issue"] = (envelopeIssueNumber ?? issueNumber).ToString(),
                ["workflowrunid"] = workflowRunId,
                ["stage"] = "test",
            });
    }

    public async Task AppendAgentSessionEventAsync(
        string sessionId,
        string projectId,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null,
        int? envelopeIssueNumber = 1,
        int? envelopeEpicNumber = 7,
        bool includeIssueContext = true)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectid"] = projectId,
            ["sessionid"] = sessionId,
        };
        if (includeIssueContext)
        {
            if (envelopeIssueNumber is > 0)
                extensions["issue"] = envelopeIssueNumber.Value.ToString();
            if (envelopeEpicNumber is > 0)
                extensions["epic"] = envelopeEpicNumber.Value.ToString();
        }

        await AppendEventAsync(
            AgentSessionEventPersistence.AgentSessionSource(sessionId),
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: extensions);
    }

    public async Task AppendEpicEventAsync(
        string projectId,
        int epicNumber,
        string type,
        DateTimeOffset? time = null,
        string? subject = null,
        object? data = null)
    {
        await AppendEventAsync(
            EpicEventPersistence.EpicSource(projectId, epicNumber),
            type,
            time ?? FixedTime,
            subject,
            data,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["epic"] = epicNumber.ToString(),
            });
    }

    public async Task AppendEventAsync(
        string source,
        string type,
        DateTimeOffset time,
        string? subject,
        object? data,
        Dictionary<string, string> extensions)
    {
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var dataElement = data is null
            ? JsonDocument.Parse("null").RootElement.Clone()
            : JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions);
        var envelope = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: time,
            data: dataElement,
            subject: subject,
            extensions: extensions);
        await store.AppendAsync(envelope);
    }

    public async Task SeedIssueEventHistoryAsync(string projectId, int issueNumber, int count)
    {
        await using var db = await CreateDbAsync();
        var source = IssueEventPersistence.IssueSource(projectId, issueNumber);
        db.IssueEvents.AddRange(Enumerable.Range(1, count).Select(index => new IssueEventRow
        {
            Id = index,
            Source = source,
            EventId = $"history-{index}",
            Type = "com.mohist.issue.created",
            Time = FixedTime.AddSeconds(index),
            SpecVersion = "1.0",
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(new { }, CloudEvent.JsonOptions),
            ExtensionsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["projectid"] = projectId,
                ["issue"] = issueNumber.ToString(),
            }),
        }));
        await db.SaveChangesAsync();
    }

    private async Task<MohistDbContext> CreateDbAsync()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        return await factory.CreateDbContextAsync();
    }
}
