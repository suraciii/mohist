using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services;

public sealed class IssueListItem
{
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string Status { get; set; } = "backlog";
    [JsonPropertyName("health")]
    public string Health { get; set; } = "active";
    public string ProjectId { get; set; } = null!;
    public string? ProjectName { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
    public string Priority { get; set; } = "p2";
    public string? Risk { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
    public string? CompletedAt { get; set; }
    [JsonPropertyName("approvalState")]
    public StageApproval? StageApproval { get; set; }
    public string? BlockedReason { get; set; }
    public string? WorkflowRunId { get; set; }
    public string? WorkflowStage { get; set; }
    public string? WorkflowStatus { get; set; }
    [JsonPropertyName("workflowStageProgress")]
    public WorkflowStageProgress? WorkflowStageProgress { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? WorkflowProfileId { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    [JsonPropertyName("prereq")]
    public IssuePrerequisiteSummary[] Prereq { get; set; } = [];
    public bool IsDraft { get; set; }
    public bool CanStart { get; set; }
    public bool CanBeParent { get; set; }
    public IssueStartBlockerDto? Blocker { get; set; }
    public string? RepositoryName { get; set; }
    public RepositoryInfo? Repository { get; set; }
    public IssueRepositoryProblem? RepositoryProblem { get; set; }
    [JsonPropertyName("epic")]
    public IssuePrimaryEpic? Epic { get; set; }
    public IssueParentRef? ParentIssueRef { get; set; }
    public ChildIssuesSummary? ChildIssuesSummary { get; set; }
    public IssueChildRef[] Children { get; set; } = [];
    public IssueWatchEntryDto[] Watching { get; set; } = [];
    public IssueWatchEntryDto[] Muted { get; set; } = [];

    public static IssueListItem FromReadModel(IssueReadModel issue) => new()
    {
        Number = issue.Number,
        Title = issue.Title,
        Status = issue.Status,
        Health = issue.Health,
        ProjectId = issue.ProjectId,
        ProjectName = issue.ProjectName,
        Labels = issue.Labels,
        Priority = issue.Priority,
        Risk = issue.Risk,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        CompletedAt = issue.CompletedAt,
        StageApproval = issue.StageApproval,
        BlockedReason = issue.BlockedReason,
        WorkflowRunId = issue.WorkflowRunId,
        WorkflowStage = issue.WorkflowStage,
        WorkflowStatus = issue.WorkflowStatus,
        WorkflowStageProgress = issue.WorkflowStageProgress,
        WorkflowProfileId = issue.WorkflowProfileId,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
        Prereq = issue.Prereq,
        IsDraft = issue.IsDraft,
        CanStart = issue.CanStart,
        CanBeParent = issue.CanBeParent,
        Blocker = issue.Blocker,
        RepositoryName = issue.RepositoryName,
        Repository = issue.Repository,
        RepositoryProblem = issue.RepositoryProblem,
        Epic = issue.Epic,
        ParentIssueRef = issue.ParentIssueRef,
        ChildIssuesSummary = issue.ChildIssuesSummary,
        Children = issue.Children,
        Watching = issue.Watching,
        Muted = issue.Muted,
    };
}

public sealed record IssueParentCandidate(int Number, string Title);
