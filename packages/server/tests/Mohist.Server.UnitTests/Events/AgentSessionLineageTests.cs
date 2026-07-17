using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// Pure-helper unit tests for <see cref="AgentSessionLineage"/>. Mirrors the
/// T-004 <c>EpicLineageTests</c> skeleton: a hand-constructed
/// <see cref="AgentSession"/> with <c>Metadata.Labels</c> drives the helper,
/// and the resulting extensions are asserted against the matrix pinned in
/// the T-001 section of the issue-412 progress file. Stamping must read
/// ONLY from the session's own labels — no DB context, no grain call, no
/// cross-aggregate query (D6).
/// </summary>
public class AgentSessionLineageTests
{
    private const string ProjectId = "proj_agent_lineage";
    private const string AgentId = "agent_lineage_1";
    private const string AgentName = "agent-lineage-name";
    private const string SessionId = "sess_lineage_1";
    private const string WorkflowRunId = "wr_lineage_1";
    private const string Stage = "build";
    private const int IssueNumber = 42;
    private const int EpicNumber = 7;
    private static readonly DateTime FixedTime = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildExtensions_AgentLaunchSession_StampsProjectSessionAndAgentId()
    {
        var session = BuildAgentLaunchSession();

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.Equal(ProjectId, extensions["projectid"]);
        Assert.Equal(SessionId, extensions["sessionid"]);
        Assert.Equal(AgentId, extensions["agentid"]);
    }

    [Fact]
    public void BuildExtensions_AgentLaunchSession_OmitsWorkflowLineage()
    {
        var session = BuildAgentLaunchSession();

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.False(extensions.ContainsKey("issue"));
        Assert.False(extensions.ContainsKey("workflowrunid"));
        Assert.False(extensions.ContainsKey("stage"));
    }

    [Fact]
    public void BuildExtensions_AgentLaunchSessionWithLocalContext_StampsIssueAndEpic()
    {
        var session = BuildAgentLaunchSession(issueNumber: IssueNumber, epicNumber: EpicNumber);

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.Equal(IssueNumber.ToString(), extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal(EpicNumber.ToString(), extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public void BuildExtensions_WorkflowOriginSession_StampsIssueWorkflowRunIdAndStage()
    {
        var session = BuildWorkflowOriginSession();

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.Equal(ProjectId, extensions["projectid"]);
        Assert.Equal(SessionId, extensions["sessionid"]);
        Assert.Equal(IssueNumber.ToString(), extensions["issue"]);
        Assert.Equal(WorkflowRunId, extensions["workflowrunid"]);
        Assert.Equal(Stage, extensions["stage"]);
    }

    [Fact]
    public void BuildExtensions_WorkflowOriginSession_OmitsAgentId()
    {
        // A workflow-origin session does not carry agentid — it is an
        // agent-launch-only attribute per D6.
        var session = BuildWorkflowOriginSession();

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.False(extensions.ContainsKey("agentid"));
    }

    [Fact]
    public void BuildExtensions_WorkflowOriginSession_StampsEpicWhenLabeled()
    {
        var session = BuildWorkflowOriginSession(epicNumber: EpicNumber);

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.Equal(EpicNumber.ToString(), extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public void BuildExtensions_WorkflowOriginSessionWithoutIssueNumber_OmitsIssueKey()
    {
        // A workflow session whose labels lack the issue-number label
        // omits the `issue` key (absent affiliation is omitted, never an
        // empty value).
        var session = BuildWorkflowOriginSession(includeIssueNumber: false);

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.False(extensions.ContainsKey("issue"));
        Assert.Equal(WorkflowRunId, extensions["workflowrunid"]);
        Assert.Equal(Stage, extensions["stage"]);
    }

    [Fact]
    public void BuildExtensions_EmptyLabels_FailsBecauseProjectOwnershipIsRequired()
    {
        // A session with no labels still gets sessionid stamped (the
        // session id is the producer's own identity). projectid and
        // agentid/issue/workflowrunid/stage are absent, never empty.
        var session = new AgentSession
        {
            Id = SessionId,
            Runtime = new AgentSessionRuntime("runner-1", null),
            Settings = new AgentSessionSettings("opencode"),
        };
        session.Status = session.Status with
        {
            CreatedAt = FixedTime,
            LastDataAt = FixedTime,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => AgentSessionLineage.BuildExtensions(session));

        Assert.Contains("project-id", ex.Message);
    }

    [Fact]
    public void BuildExtensions_AbsentProjectLabel_FailsBecauseProjectOwnershipIsRequired()
    {
        // projectid is stamped only when the project-id label is present.
        // Whitespace-only label is treated as absent — omission IS the
        // contract.
        var session = new AgentSession
        {
            Id = SessionId,
            Runtime = new AgentSessionRuntime("runner-1", null),
            Settings = new AgentSessionSettings("opencode"),
        };
        session.Status = session.Status with { CreatedAt = FixedTime, LastDataAt = FixedTime };
        session.Metadata = session.Metadata
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, "   ");

        var ex = Assert.Throws<InvalidOperationException>(() => AgentSessionLineage.BuildExtensions(session));

        Assert.Contains("project-id", ex.Message);
    }

    [Fact]
    public void BuildExtensions_NoCrossAggregateLoad_TakesOnlySessionState()
    {
        // The helper takes only the session instance. A future refactor
        // that added a DB query or grain call would change the helper's
        // arity and surface here — same defensive shape as the other
        // lineage helpers.
        var session = BuildAgentLaunchSession();

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.Equal(SessionId, extensions[EventCatalog.Lineage.SessionId]);
        Assert.Equal(ProjectId, extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(AgentId, extensions[EventCatalog.Lineage.AgentId]);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "projectid", "sessionid", "agentid" },
            new HashSet<string>(extensions.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildExtensions_AgentLaunchSession_CarriesSessionProducerContext()
    {
        var session = BuildAgentLaunchSession();

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.Equal(ProjectId, extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(SessionId, extensions[EventCatalog.Lineage.SessionId]);
    }

    [Fact]
    public void BuildExtensions_WorkflowSession_CarriesSessionProducerContext()
    {
        var session = BuildWorkflowOriginSession();

        var extensions = AgentSessionLineage.BuildExtensions(session);

        Assert.Equal(ProjectId, extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal(SessionId, extensions[EventCatalog.Lineage.SessionId]);
    }

    private static AgentSession BuildAgentLaunchSession(int? issueNumber = null, int? epicNumber = null)
    {
        var session = new AgentSession
        {
            Id = SessionId,
            Runtime = new AgentSessionRuntime("runner-1", null),
            Settings = new AgentSessionSettings("opencode"),
        };
        session.Status = session.Status with
        {
            CreatedAt = FixedTime,
            LastDataAt = FixedTime,
        };
        session.Metadata = session.Metadata
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, ProjectId)
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
            .WithLabel(GenericAgentSessionMetadata.AgentId, AgentId)
            .WithLabel(GenericAgentSessionMetadata.AgentName, AgentName);
        if (issueNumber is not null)
            session.Metadata = session.Metadata.WithLabel(GenericAgentSessionMetadata.IssueNumber, issueNumber.Value.ToString());
        if (epicNumber is not null)
            session.Metadata = session.Metadata.WithLabel(GenericAgentSessionMetadata.EpicNumber, epicNumber.Value.ToString());
        return session;
    }

    private static AgentSession BuildWorkflowOriginSession(bool includeIssueNumber = true, int? epicNumber = null)
    {
        var labels = new WorkflowAgentSessionContext(
            ProjectId: ProjectId,
            WorkflowRunId: WorkflowRunId,
            SessionName: "sess-name",
            IssueNumber: includeIssueNumber ? IssueNumber : null,
            EpicNumber: epicNumber,
            Stage: Stage);
        var session = new AgentSession
        {
            Id = SessionId,
            Runtime = new AgentSessionRuntime("runner-1", null),
            Settings = new AgentSessionSettings("opencode"),
        };
        session.Status = session.Status with
        {
            CreatedAt = FixedTime,
            LastDataAt = FixedTime,
        };
        session.Metadata = new AgentSessionMetadata(WorkflowAgentSessionMetadata.Labels(labels), null);
        return session;
    }
}
