using System.Text.Json;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.AgentOps;

public sealed class IssueEventFeedAssemblerTests
{
    [Fact]
    public void SelectNewestUsesOneCrossStoreTimeFirstKeyAndReturnsAscending()
    {
        var issue = Event(1, "issue", DateTimeOffset.Parse("2026-07-21T10:00:00Z"));
        var workflowOldIdNewTime = Event(1, "workflow", DateTimeOffset.Parse("2026-07-21T12:00:00Z"));
        var workflowNewIdOldTime = Event(2, "workflow", DateTimeOffset.Parse("2026-07-21T11:00:00Z"));
        var agent = Event(1, "agent", DateTimeOffset.Parse("2026-07-21T13:00:00Z"));

        var selected = IssueEventFeedAssembler.SelectNewest(
            [issue],
            [workflowOldIdNewTime, workflowNewIdOldTime],
            [agent],
            3);

        Assert.Equal([workflowNewIdOldTime, workflowOldIdNewTime, agent], selected);
    }

    [Fact]
    public void SelectNewestUsesOriginAndSourceForEqualTimes()
    {
        var time = DateTimeOffset.Parse("2026-07-21T10:00:00Z");
        var issue = Event(7, "same", time, "issue-event");
        var workflow = Event(7, "same", time, "workflow-event");
        var agent = Event(7, "same", time, "agent-event");

        var selected = IssueEventFeedAssembler.SelectNewest([issue], [workflow], [agent], 3);

        Assert.Equal(["issue-event", "workflow-event", "agent-event"], selected.Select(e => e.Envelope.Id));
    }

    [Fact]
    public void ProjectRoutedFailureRequiresCanonicalAgentJobDeliveryAndProjectsEnvelope()
    {
        var session = new AgentSessionRow
        {
            Id = "session-1",
            LabelProjectId = "proj-1",
            LabelSourceKind = "agent-launch",
            LabelAgentLaunchIssueNumber = "42",
            LabelAgentId = "agent-1",
            LabelAgentName = "Reviewer",
            LabelTriggerEventId = "evt-1",
            LabelTriggerRuleId = "rule-1",
        };
        var part = new AgentSessionTranscriptPartRow
        {
            Id = 9,
            CorrelationKey = "agent-job:job-1:terminal",
            CorrelationId = "agent-job:job-1:terminal",
            Type = "session.activity",
            PayloadStatus = "failed",
            PayloadJson = JsonSerializer.Serialize(new
            {
                deliveryId = "agent-job:job-1:terminal",
                status = "failed",
                exitCode = 17,
                failureReason = "workspace unavailable",
                failureCategory = "workspace-unavailable",
            }),
            LastSeenAt = DateTime.SpecifyKind(new DateTime(2026, 7, 21, 12, 34, 56), DateTimeKind.Utc),
        };

        var result = IssueEventFeedAssembler.ProjectRoutedFailure(session, part);

        Assert.NotNull(result);
        Assert.Equal(9, result.Id);
        Assert.Equal("session-1:activity:agent-job:job-1:terminal", result.Envelope.Id);
        Assert.Equal("/mohist/agent-session/session-1", result.Envelope.Source.ToString());
        Assert.Equal("session.activity", result.Envelope.Type);
        Assert.Equal("session-1", result.Envelope.Subject);
        Assert.Equal("1.0", result.Envelope.SpecVersion);
        Assert.Equal("application/json", result.Envelope.DataContentType);
        Assert.Equal("proj-1", result.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("42", result.Envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal("workspace unavailable", result.Envelope.Data!.Value.GetProperty("failureReason").GetString());
        Assert.Equal("workspace-unavailable", result.Envelope.Data!.Value.GetProperty("failureCategory").GetString());
        Assert.Equal(17, result.Envelope.Data!.Value.GetProperty("exitCode").GetInt32());
        Assert.Equal("failed", result.Envelope.Data!.Value.GetProperty("status").GetString());
        Assert.Equal("agent-1", result.Envelope.Data!.Value.GetProperty("agentId").GetString());
        Assert.Equal("Reviewer", result.Envelope.Data!.Value.GetProperty("agentName").GetString());
        Assert.Equal("evt-1", result.Envelope.Data!.Value.GetProperty("triggerEventId").GetString());
        Assert.Equal("rule-1", result.Envelope.Data!.Value.GetProperty("triggerRuleId").GetString());
    }

    [Fact]
    public void ProjectRoutedFailureExcludesRuntimeAndFollowupDeliveries()
    {
        var session = new AgentSessionRow
        {
            Id = "session-1",
            LabelProjectId = "proj-1",
            LabelAgentLaunchIssueNumber = "42",
            LabelTriggerEventId = "evt-1",
            LabelTriggerRuleId = "rule-1",
        };
        var part = new AgentSessionTranscriptPartRow
        {
            Type = "session.activity",
            CorrelationKey = "runtime-close",
            PayloadStatus = "failed",
            PayloadJson = "{\"deliveryId\":\"runtime-close\",\"status\":\"failed\"}",
        };

        Assert.Null(IssueEventFeedAssembler.ProjectRoutedFailure(session, part));
    }

    [Fact]
    public void ProjectRoutedFailureExcludesAgentJobPrefixedNonTerminalDelivery()
    {
        var session = new AgentSessionRow
        {
            Id = "session-1",
            LabelProjectId = "proj-1",
            LabelAgentLaunchIssueNumber = "42",
            LabelTriggerEventId = "evt-1",
            LabelTriggerRuleId = "rule-1",
        };
        var deliveryId = "agent-job:job-1:not-terminal";
        var part = new AgentSessionTranscriptPartRow
        {
            Type = "session.activity",
            CorrelationKey = deliveryId,
            CorrelationId = deliveryId,
            PayloadStatus = "failed",
            PayloadJson = JsonSerializer.Serialize(new
            {
                deliveryId,
                status = "failed",
            }),
        };

        Assert.Null(IssueEventFeedAssembler.ProjectRoutedFailure(session, part));
    }

    private static StoredCloudEvent Event(long id, string source, DateTimeOffset time, string? eventId = null) =>
        new(id, new CloudEvent(
            eventId ?? $"event-{source}-{id}",
            new Uri(source, UriKind.Relative),
            "test",
            time,
            JsonSerializer.SerializeToElement(new { }),
            subject: source));
}
