using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.Issue.Queries;

public class IssueInfo
{
    public string Id { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string Status { get; set; } = "backlog";
    [JsonPropertyName("health")] public string Health { get; set; } = "active";
    public string ProjectId { get; set; } = null!;
    public string? ProjectName { get; set; }
    public string[] Labels { get; set; } = [];
    public string Priority { get; set; } = "p2";
    public string? Model { get; set; }
    public Dictionary<string, string>? StageModels { get; set; }
    public Dictionary<string, Dictionary<string, string>>? StageVariables { get; set; }
    public Dictionary<string, object?>? AgentConfig { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
    [JsonPropertyName("approvalState")] public StageApproval? StageApproval { get; set; }
    public string? BlockedReason { get; set; }
    public string? WorkflowRunId { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    public string WorkflowProfileId { get; set; } = IssueWorkflowProfiles.DefaultId;
    public string? WorkflowProfileMode { get; set; }
    public WorkflowAttention? Attention { get; set; }
    public string? WorkflowStage { get; set; }
    public string? WorkflowStatus { get; set; }
    public RepositoryInfo? Repository { get; set; }
    public IssueRepositoryProblem? RepositoryProblem { get; set; }
}

[GenerateSerializer]
public class IssuePrerequisiteSummary
{
    [Id(0)] public string IssueId { get; set; } = null!;
    [Id(1)] public int Number { get; set; }
    [Id(2)] public string Title { get; set; } = null!;
    [Id(3)] public bool Completed { get; set; }
    [Id(4)] public string Stage { get; set; } = null!;
    [Id(5)] public string Status { get; set; } = null!;

    public static IssuePrerequisiteSummary FromDomain(Domain.Issue issue) => new()
    {
        IssueId = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Completed = issue.Status == IssueStatus.Done,
        Stage = MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
        Status = MohistDefaultWorkflowProjection.Health(issue.Status),
    };

    public static IssuePrerequisiteSummary FromReadModel(IssueReadModel issue) => new()
    {
        IssueId = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Completed = issue.Status == "done" || issue.Health is "done" or "completed",
        Stage = issue.Status,
        Status = issue.Health,
    };
}

[GenerateSerializer]
public class IssueStartEligibility
{
    [Id(0)] public bool Startable { get; set; }
    [Id(1)] public string Reason { get; set; } = "ready";
    [Id(2)] public string? Message { get; set; }
    [Id(3)] public IssuePrerequisiteSummary[] WaitingForCompletion { get; set; } = [];

    public static IssueStartEligibility Ready() => new() { Startable = true };

    public static IssueStartEligibility FromPrerequisites(IssuePrerequisiteSummary[] prerequisites)
    {
        var waiting = prerequisites.Where(p => !p.Completed).ToArray();
        return waiting.Length == 0
            ? Ready()
            : new IssueStartEligibility
            {
                Startable = false,
                Reason = "waiting-for-completion",
                Message = $"Waiting for #{waiting[0].Number}",
                WaitingForCompletion = waiting,
            };
    }
}

[GenerateSerializer]
public class IssuePrimaryEpic
{
    [Id(0)] public string Id { get; set; } = null!;
    [Id(1)] public string Title { get; set; } = null!;
    [Id(2)] public string Status { get; set; } = null!;
    [Id(3)] public string Priority { get; set; } = null!;
}