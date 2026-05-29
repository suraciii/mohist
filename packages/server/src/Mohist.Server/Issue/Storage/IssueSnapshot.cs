using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Queries;
using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Issue.Storage;

public sealed class IssueWorkflowProfileSnapshot
{
    public string SourceProfileId { get; set; } = IssueWorkflowProfiles.DefaultId;
    public WorkflowDefinition Definition { get; set; } = null!;

    public IssueWorkflowProfile ToDomain() => new(SourceProfileId, Definition);

    public static IssueWorkflowProfileSnapshot FromDomain(IssueWorkflowProfile profile) => new()
    {
        SourceProfileId = profile.SourceProfileId,
        Definition = profile.Definition,
    };

    public static IssueWorkflowProfile? Deserialize(string json) =>
        JsonSerializer.Deserialize<IssueWorkflowProfileSnapshot>(json)?.ToDomain();

    public static string Serialize(IssueWorkflowProfile profile) =>
        JsonSerializer.Serialize(FromDomain(profile));
}

public sealed class IssueSnapshot
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string[] Labels { get; set; } = [];
    public string Priority { get; set; } = "p2";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? WorkflowRunId { get; set; }
    [JsonPropertyName("Status")]
    public IssueStage Stage { get; set; } = IssueStage.Backlog;
    public IssueAttention? Attention { get; set; }
    [JsonPropertyName("ApprovalState")]
    public StageApproval? StageApproval { get; set; }
    public int RetryCount { get; set; }
    public int ConflictRetryCount { get; set; }
    public string? BlockedReason { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    public RepositoryInfo? Repository { get; set; }

    public static IssueSnapshot FromDomain(Domain.Issue issue) => new()
    {
        Id = issue.Id,
        ProjectId = issue.ProjectId,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Labels = issue.Labels,
        Priority = issue.Priority,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        WorkflowRunId = issue.WorkflowRunId,
        Stage = issue.Stage,
        Attention = issue.Attention,
        StageApproval = issue.StageApproval,
        RetryCount = issue.RetryCount,
        ConflictRetryCount = issue.ConflictRetryCount,
        BlockedReason = issue.BlockedReason,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
        Repository = issue.Repository,
    };

public Domain.Issue ToDomain() => new Domain.Issue
    {
        Id = Id,
        ProjectId = ProjectId,
        Number = Number,
        Title = Title,
        Body = Body,
        Labels = Labels,
        Priority = Priority,
        CreatedAt = CreatedAt == default ? DateTime.UtcNow : CreatedAt,
        UpdatedAt = UpdatedAt == default ? DateTime.UtcNow : UpdatedAt,
        ArchivedAt = ArchivedAt,
        WorkflowRunId = WorkflowRunId,
        Stage = Stage,
        Attention = Attention,
        StageApproval = StageApproval,
        RetryCount = RetryCount,
        ConflictRetryCount = ConflictRetryCount,
        BlockedReason = BlockedReason,
        PrerequisiteNumbers = PrerequisiteNumbers,
        Repository = Repository,
    };
}