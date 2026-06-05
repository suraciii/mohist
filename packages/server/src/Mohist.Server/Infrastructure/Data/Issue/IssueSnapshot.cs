using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.Infrastructure.Data.Issue;

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
    public IssueStatus Status { get; set; } = IssueStatus.Backlog;
    public int[] PrerequisiteNumbers { get; set; } = [];

    public string? RepositoryRef { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositoryInfo? Repository { get; set; }

    public static IssueSnapshot FromDomain(Mohist.Server.Issue.Domain.Issue issue) => new()
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
        Status = issue.Status,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
        RepositoryRef = issue.RepositoryRef,
        Repository = null,
    };

    public Mohist.Server.Issue.Domain.Issue ToDomain() => new Mohist.Server.Issue.Domain.Issue
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
        Status = Status,
        PrerequisiteNumbers = PrerequisiteNumbers,
        RepositoryRef = RepositoryRef ?? Repository?.Name,
    };

    public static Mohist.Server.Issue.Domain.Issue? DeserializeIssue(string json) =>
        JsonSerializer.Deserialize<IssueSnapshot>(json)?.ToDomain();
}
