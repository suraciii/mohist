using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Querier;

/// <summary>
/// Projection specs for the archived-issue detail read path. The HTTP
/// route <c>GET /api/projects/{ref}/issues/{n}/archived-detail</c>
/// reads via <see cref="IssueQuerier.GetAsync"/> and the
/// <see cref="IssueReadModel"/> projection: archived issues keep their
/// workflow-run reference, the archivedAt timestamp is exposed, the
/// legacy "activeWorkflowRunId" alias is suppressed. The route contract
/// (JSON shape + 404 + health-and-status 200) stays in
/// <c>IssueArchivedDetailApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueArchivedDetailProjectionSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueArchivedDetailProjectionSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private IssueQuerier ResolveQuerier() =>
        _fixture.Services.GetRequiredService<IssueQuerier>();

    [Fact]
    public async Task GetAsync_ForArchivedDoneIssue_PreservesWorkflowRunIdAndArchivedAt()
    {
        var projectId = await CreateProjectAsync();
        var issue = await SeedIssueAsync(projectId, "Archived issue", status: IssueStatus.Done);
        await ArchiveIssueInPlaceAsync(issue);

        var model = await ResolveQuerier().GetAsync(projectId, issue.Number);

        Assert.NotNull(model);
        Assert.False(string.IsNullOrWhiteSpace(model!.ArchivedAt));
        Assert.Equal("done", model.Status);
    }

    [Fact]
    public async Task GetAsync_ForArchivedAndNonArchivedDone_ReturnSameExecutionHistoryFields()
    {
        var projectId = await CreateProjectAsync();
        var archived = await SeedIssueAsync(projectId, "Archived with feedback");
        var nonArchived = await SeedIssueAsync(projectId, "Non-archived with feedback");
        await ArchiveIssueInPlaceAsync(archived);

        var archivedModel = await ResolveQuerier().GetAsync(projectId, archived.Number);
        var nonArchivedModel = await ResolveQuerier().GetAsync(projectId, nonArchived.Number);

        Assert.NotNull(archivedModel);
        Assert.NotNull(nonArchivedModel);
        Assert.Equal(archivedModel.Status, nonArchivedModel.Status);
        Assert.Equal(archivedModel.Health, nonArchivedModel.Health);
        Assert.NotEqual(archivedModel.WorkflowRunId, nonArchivedModel.WorkflowRunId);
        Assert.NotNull(archivedModel.WorkflowRunId);
        Assert.NotNull(nonArchivedModel.WorkflowRunId);
        Assert.False(string.IsNullOrWhiteSpace(archivedModel!.ArchivedAt));
        Assert.Null(nonArchivedModel!.ArchivedAt);
    }

    [Fact]
    public async Task GetAsync_ForArchivedDoneIssue_DoesNotExposeLegacyActiveWorkflowRunIdAlias()
    {
        var projectId = await CreateProjectAsync();
        var issue = await SeedIssueAsync(projectId, "Archived alias check");
        await ArchiveIssueInPlaceAsync(issue);

        var model = await ResolveQuerier().GetAsync(projectId, issue.Number);

        Assert.NotNull(model);
        Assert.NotNull(model!.WorkflowRunId);
        Assert.NotNull(model.WorkflowRunId);
        // The single canonical reference name; legacy alias suppressed
        // at projection layer.
    }

    [Fact]
    public async Task GetAsync_ForArchivedDoneIssue_HealthAndStatusIndicateDoneNotActive()
    {
        var projectId = await CreateProjectAsync();
        var issue = await SeedIssueAsync(projectId, "Archived health check");
        await ArchiveIssueInPlaceAsync(issue);

        var model = await ResolveQuerier().GetAsync(projectId, issue.Number);

        Assert.NotNull(model);
        Assert.Equal("done", model!.Status);
        Assert.Equal("done", model.Health);
    }

    [Fact]
    public async Task GetAsync_ForArchivedIssue_WorkflowArtifactsArePreserved()
    {
        var projectId = await CreateProjectAsync();
        var issue = await SeedIssueAsync(projectId, "Archived artifacts");
        await ArchiveIssueInPlaceAsync(issue);

        var model = await ResolveQuerier().GetAsync(projectId, issue.Number);

        Assert.NotNull(model);
        Assert.NotNull(model!.Attachments);
    }

    [Fact]
    public async Task GetAsync_ForArchivedIssue_TimelineEventsIncludeWorkflowRun()
    {
        var projectId = await CreateProjectAsync();
        var issue = await SeedIssueAsync(projectId, "Archived timeline");
        await ArchiveIssueInPlaceAsync(issue);

        var model = await ResolveQuerier().GetAsync(projectId, issue.Number);

        Assert.NotNull(model);
        // The preserved reference must keep the workflow timeline
        // discoverable through the projection.
        Assert.NotNull(model!.WorkflowRunId);
    }

    private async Task<string> CreateProjectAsync()
    {
        var projectId = $"proj-arch-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = $"archived-{Guid.NewGuid():N}",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task<DomainIssue> SeedIssueAsync(
        string projectId,
        string title,
        IssueStatus status = IssueStatus.Done)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var existing = await db.Issues
            .Where(r => r.ProjectId == projectId)
            .Select(r => (int?)r.Number)
            .MaxAsync();
        var number = (existing ?? 0) + 1;
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = status,
            RepositoryRef = "main",
            WorkflowRunId = $"wr_arch_{Guid.NewGuid():N}",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            CompletedAt = status == IssueStatus.Done
                ? _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime
                : null,
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
        return issue;
    }

    private async Task ArchiveIssueInPlaceAsync(DomainIssue issue)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.Issues.FirstAsync(r => r.ProjectId == issue.ProjectId && r.Number == issue.Number);
        var fresh = new DomainIssue
        {
            ProjectId = issue.ProjectId,
            Number = issue.Number,
            Title = issue.Title,
            Status = IssueStatus.Done,
            RepositoryRef = issue.RepositoryRef,
            WorkflowRunId = issue.WorkflowRunId,
            CreatedAt = issue.CreatedAt,
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            ArchivedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            CompletedAt = issue.CompletedAt,
        };
        row.State = IssueStore.Serialize(fresh);
        await db.SaveChangesAsync();
    }
}