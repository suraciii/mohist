using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.Issue.Services;

public class IssueInfo
{
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string Status { get; set; } = "backlog";
    [JsonPropertyName("health")] public string Health { get; set; } = "active";
    public string ProjectId { get; set; } = null!;
    public string? ProjectName { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
    public string Priority { get; set; } = "p2";
    public string? Risk { get; set; }
    public string? Model { get; set; }
    public string? ModelVariant { get; set; }
    public Dictionary<string, string>? StageModels { get; set; }
    public Dictionary<string, string>? StageModelVariants { get; set; }
    public Dictionary<string, Dictionary<string, string>>? StageVariables { get; set; }
    public Dictionary<string, object?>? AgentConfig { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
    public string? CompletedAt { get; set; }
    [JsonPropertyName("approvalState")] public StageApproval? StageApproval { get; set; }
    public string? BlockedReason { get; set; }
    public string? WorkflowRunId { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    public bool IsDraft { get; set; }
    public bool CanStart { get; set; }
    public IssueStartBlockerDto? Blocker { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? WorkflowProfileId { get; set; } = IssueWorkflowProfiles.LocalId;
    public string? WorkflowProfileMode { get; set; }
    public WorkflowAttention? Attention { get; set; }
    public string? WorkflowStage { get; set; }
    public string? WorkflowStatus { get; set; }
    public string? RepositoryName { get; set; }
    public RepositoryInfo? Repository { get; set; }
    public IssueRepositoryProblem? RepositoryProblem { get; set; }
}

[GenerateSerializer]
public class IssuePrerequisiteSummary
{
    [Id(0)] public int Number { get; set; }
    [Id(1)] public string Title { get; set; } = null!;
    [Id(2)] public bool Completed { get; set; }
    [Id(3)] public string Stage { get; set; } = null!;
    [Id(4)] public string Status { get; set; } = null!;
    [Id(5)] public string Health { get; set; } = null!;

    public static IssuePrerequisiteSummary FromDomain(Domain.Issue issue) => new()
    {
        Number = issue.Number,
        Title = issue.Title,
        Completed = issue.Status == IssueStatus.Done,
        Stage = MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
        Status = MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
        Health = MohistDefaultWorkflowProjection.Health(issue.Status),
    };

    public static IssuePrerequisiteSummary FromReadModel(IssueReadModel issue) => new()
    {
        Number = issue.Number,
        Title = issue.Title,
        Completed = issue.Status == "done" || issue.Health is "done" or "completed",
        Stage = issue.Status,
        Status = issue.Status,
        Health = issue.Health,
    };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(IssueStartBlockerDto.DraftBlocker), "draft")]
[JsonDerivedType(typeof(IssueStartBlockerDto.ParentHasChildrenBlocker), "parent-has-children")]
[JsonDerivedType(typeof(IssueStartBlockerDto.WaitingForBlocker), "waiting-for")]
[GenerateSerializer]
public abstract class IssueStartBlockerDto
{
    [JsonIgnore]
    public string Kind => this switch
    {
        DraftBlocker => "draft",
        ParentHasChildrenBlocker => "parent-has-children",
        WaitingForBlocker => "waiting-for",
        _ => string.Empty,
    };

    [GenerateSerializer]
    public sealed class DraftBlocker : IssueStartBlockerDto;

    [GenerateSerializer]
    public sealed class ParentHasChildrenBlocker : IssueStartBlockerDto;

    [GenerateSerializer]
    public sealed class WaitingForBlocker : IssueStartBlockerDto
    {
        [Id(0)] public IssuePrerequisiteRefDto Issue { get; set; } = null!;
    }

    public static IssueStartBlockerDto? FromDomain(IssueStartBlocker? blocker) => blocker switch
    {
        null => null,
        IssueStartBlocker.Draft => new DraftBlocker(),
        IssueStartBlocker.ParentHasChildren => new ParentHasChildrenBlocker(),
        IssueStartBlocker.WaitingFor waiting => new WaitingForBlocker
        {
            Issue = new IssuePrerequisiteRefDto
            {
                Number = waiting.PrerequisiteNumber,
            },
        },
        _ => null,
    };

    public static IssueStartBlockerDto? FromDomain(
        IssueStartBlocker? blocker,
        IReadOnlyDictionary<int, IssuePrerequisiteSummary>? summariesByNumber)
    {
        if (blocker is null) return null;
        if (blocker is IssueStartBlocker.Draft) return new DraftBlocker();
        if (blocker is IssueStartBlocker.ParentHasChildren) return new ParentHasChildrenBlocker();
        if (blocker is IssueStartBlocker.WaitingFor waiting)
        {
            var summary = summariesByNumber is not null && summariesByNumber.TryGetValue(waiting.PrerequisiteNumber, out var s)
                ? s
                : null;
            return new WaitingForBlocker
            {
                Issue = summary is null
                    ? new IssuePrerequisiteRefDto { Number = waiting.PrerequisiteNumber }
                    : IssuePrerequisiteRefDto.FromSummary(summary),
            };
        }
        return null;
    }
}

[GenerateSerializer]
public class IssuePrerequisiteRefDto
{
    [Id(0)] public int Number { get; set; }
    [Id(1)] public string Title { get; set; } = "";
    [Id(2)] public string Stage { get; set; } = "";
    [Id(3)] public string Status { get; set; } = "";

    public static IssuePrerequisiteRefDto FromSummary(IssuePrerequisiteSummary summary) => new()
    {
        Number = summary.Number,
        Title = summary.Title,
        Stage = summary.Stage,
        Status = summary.Status,
    };
}

[GenerateSerializer]
public sealed record IssueStartReadiness(
    [property: Id(0)] bool IsDraft,
    [property: Id(1)] bool CanStart,
    [property: Id(2)] IssueStartBlockerDto? Blocker);

[GenerateSerializer]
public class IssuePrimaryEpic
{
    [Id(0)] public int? Number { get; set; }
    [Id(1)] public string Title { get; set; } = null!;
    [Id(2)] public string Status { get; set; } = null!;
    [Id(3)] public string Priority { get; set; } = null!;
}
