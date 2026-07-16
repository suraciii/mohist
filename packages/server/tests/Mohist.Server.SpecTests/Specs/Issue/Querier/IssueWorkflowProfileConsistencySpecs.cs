using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

/// <summary>
/// Covers the issue-workflow-profile consistency fix (issue #257):
/// every read surface — issue detail, list, and workflow-profile endpoint —
/// MUST project the same effective workflow profile id resolved via
/// <see cref="EffectiveWorkflowProfileResolver"/>.
/// </summary>
[Collection("MohistDb")]
public class IssueWorkflowProfileConsistencySpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueWorkflowProfileConsistencySpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetAsync_NoStoredSelection_ResolvesToMohistLocal()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-wfp-default-{Guid.NewGuid():N}", Name = "Default Project" };
        await db.Projects.AddAsync(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = project.Id,
            Name = project.Name,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        });
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_wfp_default_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Default profile",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Equal(IssueWorkflowProfiles.LocalId, detail!.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetAsync_ExplicitPrSelection_ProjectsMohistPr()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-wfp-pr-{Guid.NewGuid():N}", Name = "PR Project" };
        await db.Projects.AddAsync(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = project.Id,
            Name = project.Name,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        });
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_wfp_pr_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "PR profile",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            WorkflowProfileId = "mohist/github-pr",
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Equal("mohist/github-pr", detail!.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_ExplicitPrSelection_ProjectsMohistPr()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-wfp-list-{Guid.NewGuid():N}", Name = "List Project" };
        await db.Projects.AddAsync(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = project.Id,
            Name = project.Name,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        });
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_wfp_list_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "PR profile listed",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            WorkflowProfileId = "mohist/github-pr",
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Equal("mohist/github-pr", item.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetAndListAsync_AgreeOnExplicitSelection()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-wfp-agree-{Guid.NewGuid():N}", Name = "Agree Project" };
        await db.Projects.AddAsync(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = project.Id,
            Name = project.Name,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        });
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_wfp_agree_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Agree profile",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            WorkflowProfileId = "mohist/github-pr",
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await service.GetAsync(project.Id, issue.Number, project);
        var list = await service.ListAsync(project.Id, project);

        Assert.NotNull(detail);
        Assert.Equal("mohist/github-pr", detail!.WorkflowProfileId);
        var item = Assert.Single(list);
        Assert.Equal(detail.WorkflowProfileId, item.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetAsync_UnknownIdSelection_FallsBackToSystemDefault()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-wfp-unknown-{Guid.NewGuid():N}", Name = "Unknown Project" };
        await db.Projects.AddAsync(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = project.Id,
            Name = project.Name,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        });
        // Manually craft state so we can simulate a legacy/corrupted row
        // pointing at an id that is not in the registry. The aggregate
        // setter normalizes whitespace but otherwise stores any string.
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_wfp_unknown_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Unknown profile",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        issue.ReplaceWorkflowProfile("team/missing");
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Equal(IssueWorkflowProfiles.LocalId, detail!.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetAndListAsync_WhenAllProfilesDisabled_ReportUnresolvedInsteadOfLocalFallback()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-wfp-none-{Guid.NewGuid():N}", Name = "No Enabled Project" };
        await db.Projects.AddAsync(new ProjectRow
        {
            Id = project.Id,
            Name = project.Name,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        });
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = project.Id,
            Variables = "{}",
            DisabledWorkflowProfileIds = ["mohist/local", "mohist/github-pr"],
        });
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_wfp_none_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "No enabled profile",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await service.GetAsync(project.Id, issue.Number, project);
        var info = await service.GetInfoAsync(project.Id, issue.Number, project);
        var list = await service.ListAsync(project.Id, project);

        Assert.NotNull(detail);
        Assert.Null(detail!.WorkflowProfileId);
        Assert.NotNull(info);
        Assert.Null(info!.WorkflowProfileId);
        var item = Assert.Single(list);
        Assert.Null(item.WorkflowProfileId);
    }
}
