using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Tests.Support.TestData;

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
            "opencode",
            null,
            metadata: BuildMetadata(projectId, issueNumber, workflowRunId, sessionName, workId, workType, stage, title));
        var row = new AgentSessionRow
        {
            Id = id,
            ProjectId = projectId,
            IssueNumber = issueNumber,
            WorkflowRunId = workflowRunId,
            SessionName = sessionName,
            RunnerId = runnerId,
            WorkId = workId,
            WorkType = workType,
            Stage = stage,
            Status = "running",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        return (session, row);
    }

    private static AgentSessionMetadata BuildMetadata(
        string projectId, int issueNumber, string workflowRunId, string sessionName,
        string? workId, string? workType, string? stage, string title) =>
        new AgentSessionMetadata()
            .WithLabel(AgentSessionMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionMetadataKeys.IssueNumber, issueNumber.ToString())
            .WithLabel(AgentSessionMetadataKeys.SourceKind, AgentSessionKey.Workflow)
            .WithLabel(AgentSessionMetadataKeys.SourceId, workflowRunId)
            .WithLabel(AgentSessionMetadataKeys.SessionName, sessionName)
            .WithLabel(AgentSessionMetadataKeys.WorkId, workId)
            .WithLabel(AgentSessionMetadataKeys.WorkType, workType)
            .WithLabel(AgentSessionMetadataKeys.Stage, stage)
            .WithAnnotation(AgentSessionMetadataKeys.Title, title);
}
