using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Querier;

[Collection("MohistDb")]
public class IssueQuerierTests
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_ReadsIssueStateWithoutCallingIssueGrain()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-1", Name = "Project One" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Query me",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" },
            Priority = "p1",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project, stage: "backlog", label: "stream=frontend");

        var item = Assert.Single(list);
        Assert.Equal("Query me", item.Title);
        Assert.Equal("backlog", item.Status);
        Assert.Equal("Project One", item.ProjectName);
    }

    [Fact]
    public async Task GetAndListAsync_ReadIssueIdKeyedRows()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-id-keyed-1", Name = "Id Keyed Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_id_keyed_1",
            ProjectId = project.Id,
            Number = 1,
            Title = "Id keyed issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["module"] = "auth" },
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
        var identities = scope.ServiceProvider.GetRequiredService<IssueIdentityResolver>();

        var loaded = await service.GetAsync(project.Id, issue.Number, project);
        var listed = await service.ListAsync(project.Id, project);
        var identity = await identities.GetAsync(project.Id, issue.Number);

        Assert.NotNull(loaded);
        Assert.Equal(issue.Id, loaded.Id);
        Assert.Equal(IssueWorkflowProfiles.LocalId, loaded.WorkflowProfileId);
        Assert.NotNull(identity);
        Assert.Equal(issue.Id, identity.IssueId);
        var item = Assert.Single(listed);
        Assert.Equal(issue.Id, item.Id);
        Assert.Equal(IssueWorkflowProfiles.LocalId, item.WorkflowProfileId);
    }

    [Fact]
    public async Task ListAsync_WithCanonicalRows_ReturnsIssueOnce()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dual-key-{Guid.NewGuid():N}", Name = "Dual Key Project" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_dual_key_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Canonical title",
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

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Equal(issue.Id, item.Id);
        Assert.Equal("Canonical title", item.Title);
    }

    [Fact]
    public async Task ListAsync_ForDoneIssue_IncludesCompletedAt()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-list-{Guid.NewGuid():N}", Name = "CompletedAt List" };
        var completedAt = new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_list_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Done with completedAt",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Done,
            CompletedAt = completedAt,
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Equal(completedAt.ToString("o"), item.CompletedAt);
    }

    [Fact]
    public async Task DetailAsync_ForCancelledIssue_IncludesCompletedAt()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-detail-{Guid.NewGuid():N}", Name = "CompletedAt Detail" };
        var completedAt = new DateTime(2026, 6, 20, 14, 0, 0, DateTimeKind.Utc);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_detail_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Cancelled with completedAt",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Cancelled,
            CompletedAt = completedAt,
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Equal(completedAt.ToString("o"), detail.CompletedAt);
    }

    [Fact]
    public async Task DetailAsync_ForNonTerminalIssue_CompletedAtIsNull()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-null-{Guid.NewGuid():N}", Name = "CompletedAt Null" };
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_null_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Backlog no completedAt",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Null(detail.CompletedAt);
    }

    [Fact]
    public async Task DetailAsync_ArchivedIssue_ExposesSameCompletedAt()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completedat-archived-{Guid.NewGuid():N}", Name = "CompletedAt Archived" };
        var completedAt = new DateTime(2026, 6, 25, 9, 15, 0, DateTimeKind.Utc);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_completedat_archived_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Archived done issue",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Done,
            CompletedAt = completedAt,
            ArchivedAt = new DateTime(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc),
        };
        db.Issues.Add(new IssueRow { IssueId = issue.Id, State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var detail = await service.GetAsync(project.Id, issue.Number, project);

        Assert.NotNull(detail);
        Assert.Equal(completedAt.ToString("o"), detail.CompletedAt);
        Assert.NotNull(detail.ArchivedAt);
    }
}
