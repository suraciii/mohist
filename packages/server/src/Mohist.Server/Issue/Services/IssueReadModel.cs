using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using System.Text.Json.Serialization;

namespace Mohist.Server.Issue.Services;

public class IssueReadModel
{
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string Status { get; set; } = "backlog";
    [JsonPropertyName("health")]
    public string Health { get; set; } = "active";
    public string ProjectId { get; set; } = null!;
    public string? ProjectName { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
    public string Priority { get; set; } = "p2";
    public string? Risk { get; set; }
    public string? Model { get; set; }
    public string? ModelVariant { get; set; }
    public Dictionary<string, object?>? AgentConfig { get; set; }
    public Dictionary<string, string>? StageModels { get; set; }
    public Dictionary<string, string>? StageModelVariants { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
    public string? CompletedAt { get; set; }
    [JsonPropertyName("approvalState")]
    public StageApproval? StageApproval { get; set; }
    public string? BlockedReason { get; set; }
    public WorkflowAttention? Attention { get; set; }
    public string? WorkflowRunId { get; set; }
    public string? WorkflowStage { get; set; }
    public string? WorkflowStatus { get; set; }
    [JsonPropertyName("workflowStageProgress")]
    public WorkflowStageProgress? WorkflowStageProgress { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? WorkflowProfileId { get; set; } = IssueWorkflowProfiles.LocalId;
    public string? WorkflowProfileMode { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    public IssueCommentDto[] Comments { get; set; } = [];
    public AttachmentInfo[] Attachments { get; set; } = [];
    public IssuePrerequisiteSummary[] Prerequisites { get; set; } = [];
    public bool IsDraft { get; set; }
    public bool CanStart { get; set; }
    public IssueStartBlockerDto? Blocker { get; set; }
    public string? RepositoryName { get; set; }
    public RepositoryInfo? Repository { get; set; }
    public IssueRepositoryProblem? RepositoryProblem { get; set; }
    public IssuePrimaryEpic? PrimaryEpic { get; set; }
    public IssueFeedbackDto[] Feedback { get; set; } = [];
}

public sealed record IssueFeedbackDto(
    string Id,
    int IssueNumber,
    string WorkflowRunId,
    string Stage,
    ApprovalFeedbackStatus Status,
    string Body,
    string CreatedAt,
    IssueFeedbackResolutionDto? Resolution = null);

public sealed record IssueFeedbackResolutionDto(
    string? ResolutionTaskId,
    string? ResolvedAt,
    string? ResolutionSummary);

public sealed record AttachmentInfo(
    string Id,
    string FileName,
    string ContentType,
    long Size);
