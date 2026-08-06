using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.SpecTests.Support.TestData;

/// <summary>
/// Centralized factory for <see cref="AgentSession"/> and
/// <see cref="AgentSessionRow"/>. All factories are deterministic so test
/// failure snapshots are reproducible.
/// </summary>
public static class AgentSessionTestData
{
    public const string DefaultProjectId = "proj-test";
    public const int DefaultIssueNumber = 1;
    public const string DefaultWorkflowRunId = "wr-test-1";
    public const string DefaultSessionName = "session-test";
    public const string DefaultRunnerId = "runner-test";

    public static (AgentSession Session, AgentSessionRow Row) CreateRunning(
        string projectId = DefaultProjectId,
        int issueNumber = DefaultIssueNumber,
        string workflowRunId = DefaultWorkflowRunId,
        string sessionName = DefaultSessionName,
        string? runnerId = DefaultRunnerId,
        string? workId = "task-1.1",
        string workType = "task",
        string stage = "build",
        string title = "Test session")
    {
        var id = $"agent_session_{projectId}_{issueNumber}_{sessionName}";
        var createdAt = new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc);
        var session = AgentSession.Create(
            id,
            runnerId ?? string.Empty,
            null,
            metadata: BuildMetadata(projectId, issueNumber, workflowRunId, sessionName, workId, workType, stage, title));
        var row = new AgentSessionRow
        {
            Id = id,
            RunnerId = runnerId,
            Status = "running",
            CreatedAt = createdAt,
        };
        return (session, row);
    }

    private static AgentSessionMetadata BuildMetadata(
        string projectId, int issueNumber, string workflowRunId, string sessionName,
        string? workId, string? workType, string? stage, string title) =>
        new AgentSessionMetadata()
            .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())
            .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)
            .WithLabel(AgentSessionQueryMetadataKeys.SessionName, sessionName)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkId, workId)
            .WithLabel(AgentSessionQueryMetadataKeys.WorkType, workType)
            .WithLabel(AgentSessionQueryMetadataKeys.Stage, stage)
            .WithAnnotation(AgentSessionQueryMetadataKeys.Title, title);
}
