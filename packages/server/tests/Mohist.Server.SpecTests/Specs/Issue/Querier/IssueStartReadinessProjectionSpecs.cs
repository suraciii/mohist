using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

/// <summary>
/// Projection specs for the start-readiness fields on
/// <see cref="IssueReadModel"/>: <c>IsDraft</c>, <c>CanStart</c>, and
/// <c>Blocker</c>. The querier resolves the read-model via
/// <c>IssueQuerier.GetAsync</c> + <c>IssueQuerier.ListAsync</c>; the
/// projection rules mirror the pure-domain decisions in
/// <see cref="Mohist.Server.SpecTests.Specs.Issue.Domain.IssueStartReadinessDomainSpecs"/>
/// but exercised through the read-model instead of the aggregate.
/// </summary>
[Collection("MohistDb")]
public class IssueStartReadinessProjectionSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueStartReadinessProjectionSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private IssueQuerier ResolveQuerier() =>
        _fixture.Services.GetRequiredService<IssueQuerier>();

    [Fact]
    public async Task GetAsync_ForDraftIssue_ProjectsDraftBlocker()
    {
        var projectId = await CreateProjectAsync();
        await SeedIssueAsync(projectId, "Draft", isDraft: true);

        var model = await ResolveQuerier().GetAsync(projectId, 1);

        Assert.NotNull(model);
        Assert.True(model!.IsDraft);
        Assert.False(model.CanStart);
        Assert.NotNull(model.Blocker);
        Assert.Equal("draft", model.Blocker!.Kind);
    }

    [Fact]
    public async Task GetAsync_ForReadyIssueWithNoPrereqs_ProjectsCanStartTrue()
    {
        var projectId = await CreateProjectAsync();
        await SeedIssueAsync(projectId, "Ready", isDraft: false);

        var model = await ResolveQuerier().GetAsync(projectId, 1);

        Assert.NotNull(model);
        Assert.False(model!.IsDraft);
        Assert.True(model.CanStart);
        Assert.Null(model.Blocker);
    }

    [Fact]
    public async Task GetAsync_ForReadyIssueWithUndeliveredPrereq_ProjectsWaitingForBlocker()
    {
        var projectId = await CreateProjectAsync();
        // prereq #1 not done; #2 waits on it
        await SeedIssueAsync(projectId, "Prereq", isDraft: false, status: IssueStatus.Backlog);
        await SeedIssueAsync(projectId, "Dependent", isDraft: false, status: IssueStatus.Backlog, prerequisiteNumbers: [1]);

        var model = await ResolveQuerier().GetAsync(projectId, 2);

        Assert.NotNull(model);
        Assert.False(model!.IsDraft);
        Assert.False(model.CanStart);
        Assert.NotNull(model.Blocker);
        Assert.Equal("waiting-for", model.Blocker!.Kind);
        Assert.IsType<IssueStartBlockerDto.WaitingForBlocker>(model.Blocker);
        var waiting = (IssueStartBlockerDto.WaitingForBlocker)model.Blocker!;
        Assert.NotNull(waiting.Issue);
        Assert.Equal(1, waiting.Issue!.Number);
    }

    [Fact]
    public async Task ListAsync_IncludesIsDraftCanStartAndBlockerPerItem()
    {
        var projectId = await CreateProjectAsync();
        await SeedIssueAsync(projectId, "Draft", isDraft: true);
        await SeedIssueAsync(projectId, "Ready", isDraft: false);

        var list = await ResolveQuerier().ListAsync(projectId, all: true);

        Assert.Equal(2, list.Count);
        var draft = list.Single(i => i.Title == "Draft");
        Assert.True(draft.IsDraft);
        Assert.False(draft.CanStart);
        Assert.NotNull(draft.Blocker);
        Assert.Equal("draft", draft.Blocker!.Kind);

        var ready = list.Single(i => i.Title == "Ready");
        Assert.False(ready.IsDraft);
        Assert.True(ready.CanStart);
        Assert.Null(ready.Blocker);
    }

    [Fact]
    public async Task GetAsync_ForReadyIssue_OmitsLegacyFields()
    {
        var projectId = await CreateProjectAsync();
        await SeedIssueAsync(projectId, "Ready", isDraft: false);

        var model = await ResolveQuerier().GetAsync(projectId, 1);

        Assert.NotNull(model);
        Assert.False(model!.IsDraft);
        Assert.True(model.CanStart);
        Assert.Null(model.Blocker);
    }

    private async Task<string> CreateProjectAsync()
    {
        var projectId = $"proj-ready-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = $"ready-{Guid.NewGuid():N}",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task SeedIssueAsync(
        string projectId,
        string title,
        bool isDraft,
        IssueStatus status = IssueStatus.Backlog,
        int[]? prerequisiteNumbers = null)
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
            IsDraft = isDraft,
            RepositoryRef = "main",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
        };
        if (prerequisiteNumbers is not null)
        {
            foreach (var p in prerequisiteNumbers)
                issue.AddPrerequisite(p);
        }
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }
}