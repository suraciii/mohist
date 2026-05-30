using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using System.Text.Json.Serialization;

namespace Mohist.Server.Issue.Queries;

public class IssueReadModel
{
    public string Id { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string Status { get; set; } = "backlog";
    [JsonPropertyName("health")]
    public string Health { get; set; } = "active";
    public string ProjectId { get; set; } = null!;
    public string? ProjectName { get; set; }
    public string[] Labels { get; set; } = [];
    public string Priority { get; set; } = "p2";
    public string? Model { get; set; }
    public Dictionary<string, object?>? AgentConfig { get; set; }
    public Dictionary<string, string>? StageModels { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
    [JsonPropertyName("approvalState")]
    public StageApproval? StageApproval { get; set; }
    public string? BlockedReason { get; set; }
    public IssueAttention? Attention { get; set; }
    public string? WorkflowRunId { get; set; }
    public string? WorkflowStage { get; set; }
    public string? WorkflowStatus { get; set; }
    public string WorkflowProfileId { get; set; } = IssueWorkflowProfiles.DefaultId;
    public int[] PrerequisiteNumbers { get; set; } = [];
    public IssueCommentDto[] Comments { get; set; } = [];
    public IssuePrerequisiteSummary[] Prerequisites { get; set; } = [];
    public IssueStartEligibility StartEligibility { get; set; } = IssueStartEligibility.Ready();
    public RepositoryInfo? Repository { get; set; }
    public IssuePrimaryEpic? PrimaryEpic { get; set; }
}