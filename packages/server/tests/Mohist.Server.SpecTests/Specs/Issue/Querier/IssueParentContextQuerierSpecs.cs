using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

[Collection("MohistDb")]
public sealed class IssueParentContextQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueParentContextQuerierSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChildResolvesOnlyCurrentParentTitleAndBody()
    {
        var projectId = $"proj-parent-context-{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, projectId, 1, "Original parent", "Original parent body");
        await SeedIssueAsync(db, projectId, 2, "Child", "Child body", parentIssueNumber: 1);
        await SeedIssueAsync(db, projectId, 3, "Sibling-only title", "Sibling-only body", parentIssueNumber: 1);
        db.IssueComments.Add(new IssueCommentRow
        {
            Id = $"comment-{Guid.NewGuid():N}",
            ProjectId = projectId,
            IssueNumber = 1,
            Body = "Parent-only comment",
            CreatedAt = TestTime.UtcDateTime,
        });
        db.Attachments.Add(new AttachmentRow
        {
            Id = $"attachment-{Guid.NewGuid():N}",
            ProjectId = projectId,
            OwnerKind = "issue",
            OwnerIssueNumber = 1,
            OriginalFileName = "parent-only.txt",
            Size = 10,
            StoragePath = "/virtual/parent-only.txt",
            CreatedAt = TestTime.UtcDateTime,
        });
        await db.SaveChangesAsync();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var original = await querier.GetParentIssueContextAsync(projectId, 2);
        await UpdateIssueAsync(db, projectId, 1, "Current parent", "Current parent body");
        var current = await querier.GetParentIssueContextAsync(projectId, 2);

        Assert.Equal(new ParentIssueContext("Original parent", "Original parent body"), original);
        Assert.Equal(new ParentIssueContext("Current parent", "Current parent body"), current);
        Assert.Equal(["Body", "Title"], typeof(ParentIssueContext).GetProperties().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public async Task OrdinaryAndDetachedIssuesResolveNoParentContext()
    {
        var projectId = $"proj-parent-context-none-{Guid.NewGuid():N}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, projectId, 1, "Parent", "Parent body");
        await SeedIssueAsync(db, projectId, 2, "Child", "Child body", parentIssueNumber: 1);
        await SeedIssueAsync(db, projectId, 3, "Ordinary", "Ordinary body");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        Assert.NotNull(await querier.GetParentIssueContextAsync(projectId, 2));
        Assert.Null(await querier.GetParentIssueContextAsync(projectId, 3));

        await DetachIssueAsync(db, projectId, 2);

        Assert.Null(await querier.GetParentIssueContextAsync(projectId, 2));
    }

    private static async Task SeedIssueAsync(
        MohistDbContext db,
        string projectId,
        int number,
        string title,
        string? body,
        int? parentIssueNumber = null)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Body = body,
            Status = IssueStatus.Backlog,
            Priority = "p2",
            ParentIssueNumber = parentIssueNumber,
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
            ParentIssueNumber = parentIssueNumber,
        });
        await db.SaveChangesAsync();
    }

    private static async Task UpdateIssueAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        string title,
        string? body)
    {
        var row = await db.Issues.SingleAsync(issue => issue.ProjectId == projectId && issue.Number == issueNumber);
        var state = IssueStore.Deserialize(row.State)!;
        var updated = new DomainIssue
        {
            ProjectId = state.ProjectId,
            Number = state.Number,
            Title = title,
            Body = body,
            Status = state.Status,
            Priority = state.Priority,
            Risk = state.Risk,
            CreatedAt = state.CreatedAt,
            UpdatedAt = TestTime.UtcDateTime,
            ArchivedAt = state.ArchivedAt,
            CompletedAt = state.CompletedAt,
            WorkflowRunId = state.WorkflowRunId,
            ParentIssueNumber = state.ParentIssueNumber,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        row.State = IssueStore.Serialize(updated);
        await db.SaveChangesAsync();
    }

    private static async Task DetachIssueAsync(MohistDbContext db, string projectId, int issueNumber)
    {
        var row = await db.Issues.SingleAsync(issue => issue.ProjectId == projectId && issue.Number == issueNumber);
        var state = IssueStore.Deserialize(row.State)!;
        var detached = new DomainIssue
        {
            ProjectId = state.ProjectId,
            Number = state.Number,
            Title = state.Title,
            Body = state.Body,
            Status = state.Status,
            Priority = state.Priority,
            Risk = state.Risk,
            CreatedAt = state.CreatedAt,
            UpdatedAt = TestTime.UtcDateTime,
            ArchivedAt = state.ArchivedAt,
            CompletedAt = state.CompletedAt,
            WorkflowRunId = state.WorkflowRunId,
            ParentIssueNumber = null,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        row.State = IssueStore.Serialize(detached);
        row.ParentIssueNumber = null;
        await db.SaveChangesAsync();
    }
}
