using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public sealed class ProducerConformanceTests
{
    [Fact]
    public void Assert_AcceptsEachProducerFamilyWithItsLocalContext()
    {
        ProducerConformance.Assert(
            EventProducerFamily.WorkflowRun,
            Extensions(("projectid", "proj"), ("workflowrunid", "run"), ("issue", "42"), ("epic", "7"), ("stage", "build")),
            new(ProjectId: "proj", Issue: "42", Epic: "7", WorkflowRunId: "run", Stage: "build", StageRequired: true));
        ProducerConformance.Assert(
            EventProducerFamily.Issue,
            Extensions(("projectid", "proj"), ("issue", "42"), ("epic", "7")),
            new(ProjectId: "proj", Issue: "42", Epic: "7"));
        ProducerConformance.Assert(
            EventProducerFamily.Epic,
            Extensions(("projectid", "proj"), ("epic", "7")),
            new(ProjectId: "proj", Epic: "7"));
        ProducerConformance.Assert(
            EventProducerFamily.AgentSession,
            Extensions(("projectid", "proj"), ("sessionid", "session"), ("agentid", "agent")),
            new(ProjectId: "proj", SessionId: "session", AgentId: "agent"));
        ProducerConformance.Assert(
            EventProducerFamily.AgentJob,
            Extensions(("agentid", "agent"), ("projectid", "proj"), ("issue", "42")),
            new(ProjectId: "proj", Issue: "42", AgentId: "agent"));
        ProducerConformance.Assert(
            EventProducerFamily.RawAgentJob,
            Extensions(("projectid", "proj")),
            new(ProjectId: "proj"));
        ProducerConformance.Assert(
            EventProducerFamily.Runner,
            Extensions(("runnerid", "runner"), ("projectid", "proj")),
            new(ProjectId: "proj", RunnerId: "runner"));
        ProducerConformance.Assert(
            EventProducerFamily.InboxItemPersisted,
            Extensions(("projectid", "proj"), ("issue", "42"), ("epic", "7"), ("workflowrunid", "run"), ("stage", "build")),
            new(ProjectId: "proj", Issue: "42", Epic: "7", WorkflowRunId: "run", Stage: "build"));
    }

    [Theory]
    [InlineData(EventProducerFamily.WorkflowRun, "projectid")]
    [InlineData(EventProducerFamily.Issue, "issue")]
    [InlineData(EventProducerFamily.Epic, "epic")]
    [InlineData(EventProducerFamily.AgentSession, "sessionid")]
    [InlineData(EventProducerFamily.AgentJob, "agentid")]
    [InlineData(EventProducerFamily.Runner, "runnerid")]
    [InlineData(EventProducerFamily.InboxItemPersisted, "issue")]
    public void Assert_RejectsMissingRequiredContext(EventProducerFamily family, string missingKey)
    {
        var extensions = Extensions(
            ("projectid", "proj"),
            ("issue", "42"),
            ("epic", "7"),
            ("workflowrunid", "run"),
            ("sessionid", "session"),
            ("agentid", "agent"),
            ("runnerid", "runner"));
        extensions.Remove(missingKey);

        var context = family switch
        {
            EventProducerFamily.WorkflowRun => new ProducerLineageContext(ProjectId: "proj", WorkflowRunId: "run"),
            EventProducerFamily.Issue => new ProducerLineageContext(ProjectId: "proj", Issue: "42"),
            EventProducerFamily.Epic => new ProducerLineageContext(ProjectId: "proj", Epic: "7"),
            EventProducerFamily.AgentSession => new ProducerLineageContext(ProjectId: "proj", SessionId: "session"),
            EventProducerFamily.AgentJob => new ProducerLineageContext(AgentId: "agent"),
            EventProducerFamily.Runner => new ProducerLineageContext(RunnerId: "runner"),
            EventProducerFamily.InboxItemPersisted => new ProducerLineageContext(ProjectId: "proj", Issue: "42"),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

        Assert.Throws<ProducerConformanceException>(() => ProducerConformance.Assert(family, extensions, context));
    }

    [Fact]
    public void Assert_RejectsAgentIdentityOnRawAgentJob()
    {
        var extensions = Extensions(("agentid", "agent"));

        Assert.Throws<ProducerConformanceException>(() => ProducerConformance.Assert(
            EventProducerFamily.RawAgentJob,
            extensions,
            new()));
    }

    [Fact]
    public void Assert_RejectsEmptyOptionalContext()
    {
        var extensions = Extensions(("projectid", "proj"), ("issue", "42"), ("epic", ""));

        Assert.Throws<ProducerConformanceException>(() => ProducerConformance.Assert(
            EventProducerFamily.Issue,
            extensions,
            new(ProjectId: "proj", Issue: "42")));
    }

    [Fact]
    public void Assert_RejectsLegacyLineageKeys()
    {
        var extensions = Extensions(("projectid", "proj"), ("issue", "42"), ("issueid", "legacy"));

        var exception = Assert.Throws<ProducerConformanceException>(() => ProducerConformance.Assert(
            EventProducerFamily.Issue,
            extensions,
            new(ProjectId: "proj", Issue: "42")));

        Assert.Contains("issueid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Assert_RejectsStageOnNonStageWorkflowEvent()
    {
        var extensions = Extensions(("projectid", "proj"), ("workflowrunid", "run"), ("stage", "build"));

        Assert.Throws<ProducerConformanceException>(() => ProducerConformance.Assert(
            EventProducerFamily.WorkflowRun,
            extensions,
            new(ProjectId: "proj", WorkflowRunId: "run")));
    }

    private static Dictionary<string, string> Extensions(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
