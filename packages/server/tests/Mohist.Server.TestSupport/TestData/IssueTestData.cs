using System.Text.Json;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Services;

namespace Mohist.Server.TestSupport.TestData;

/// <summary>
/// Centralized factory methods for the <see cref="Mohist.Server.Issue.Domain.Issue"/>
/// domain object and its <see cref="ProjectInfo"/>. Replaces inline
/// <c>private static Issue Create*</c> methods scattered across spec files.
/// All factories are deterministic (no random IDs, no <c>TestTime.UtcDateTime</c>)
/// so test failure snapshots are reproducible.
/// </summary>
public static class IssueTestData
{
    public const string DefaultProjectId = "proj-test";
    public const int DefaultNumber = 1;
    public const string DefaultTitle = "Test issue";
    public const string DefaultPriority = "p2";
    public const IssueStatus DefaultStatus = IssueStatus.Backlog;

    private static readonly DateTime DefaultTimestamp = new(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc);

    public static Mohist.Server.Issue.Domain.Issue Create(
        string projectId = DefaultProjectId,
        int number = DefaultNumber,
        string title = DefaultTitle,
        IssueStatus status = DefaultStatus,
        string priority = DefaultPriority,
        IReadOnlyDictionary<string, string>? labels = null,
        string? body = null,
        string? repositoryRef = null)
    {
        return new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Body = body,
            Status = status,
            Priority = priority,
            Labels = labels ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CreatedAt = DefaultTimestamp,
            UpdatedAt = DefaultTimestamp,
            RepositoryRef = repositoryRef,
        };
    }

    public static Mohist.Server.Issue.Domain.Issue CreateInProgress(
        string projectId = DefaultProjectId,
        int number = DefaultNumber,
        string title = DefaultTitle) =>
        Create(projectId, number, title, IssueStatus.InProgress);

    public static ProjectInfo CreateProject(
        string id = DefaultProjectId,
        string name = "Test Project") =>
        new()
        {
            Id = id,
            Name = name,
        };
}
