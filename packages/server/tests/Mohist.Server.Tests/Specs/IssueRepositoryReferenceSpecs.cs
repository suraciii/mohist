using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Querying;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Querying;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueRepositoryReferenceSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueRepositoryReferenceSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task NewIssue_WithExplicitRepository_PersistsOnlyTheReference()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, 1, "Explicit", "body", null, "p2", "secondary", issueId);

        var storedJson = await LoadStateAsync(projectId, 1);
        using var doc = JsonDocument.Parse(storedJson);

        Assert.True(doc.RootElement.TryGetProperty("RepositoryRef", out var refElement));
        Assert.Equal("secondary", refElement.GetString());
        Assert.False(
            doc.RootElement.TryGetProperty("Repository", out _),
            "New issues must not persist a mutable repository configuration snapshot as authority.");
    }

    [Fact]
    public async Task NewIssue_WithoutRepository_PersistsDefaultProjectRepositoryReference()
    {
        var (projectId, project) = await SetupProjectWithRepositoriesAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, 1, "Default", "body", null, "p2", null, issueId);

        var info = await GetIssueInfoAsync(projectId, 1);
        Assert.NotNull(info);
        Assert.Equal("main", info!.Repository?.Name);
        Assert.Equal("/proj/main", info.Repository?.Path);
        Assert.True(info.Repository?.IsDefault);

        var storedJson = await LoadStateAsync(projectId, 1);
        using var doc = JsonDocument.Parse(storedJson);
        Assert.True(doc.RootElement.TryGetProperty("RepositoryRef", out var refElement));
        Assert.Equal(project.DefaultRepository?.Name, refElement.GetString());
        Assert.False(
            doc.RootElement.TryGetProperty("Repository", out _),
            "Default-bound issues must not persist a repository configuration snapshot as authority.");
    }

    [Fact]
    public async Task LegacySnapshot_DerivesRepositoryReferenceFromEmbeddedName_AndDropsSnapshotFields()
    {
        var (projectId, project) = await SetupProjectWithRepositoriesAsync();
        const string legacyJson = """
            {
              "Id": "issue_legacy",
              "ProjectId": "__placeholder__",
              "Number": 1,
              "Title": "Legacy snapshot",
              "Body": "carries repository snapshot",
              "Labels": [],
              "Priority": "p2",
              "CreatedAt": "2024-01-01T00:00:00Z",
              "UpdatedAt": "2024-01-01T00:00:00Z",
              "Status": 0,
              "PrerequisiteNumbers": [],
              "Repository": {
                "Name": "main",
                "Path": "/stale/legacy-path",
                "Remote": "git@stale.example:repo.git",
                "BaseBranch": "ancient-branch",
                "IsDefault": false
              }
            }
            """;

        var legacy = legacyJson.Replace("__placeholder__", projectId, StringComparison.Ordinal);
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.Issues.Add(new IssueRow
            {
                IssueId = "issue_legacy",
                State = legacy,
            });
            await db.SaveChangesAsync();
        }

        var info = await GetIssueInfoAsync(projectId, 1);
        Assert.NotNull(info);
        Assert.Equal("main", info!.Repository?.Name);
        Assert.Equal("/proj/main", info.Repository?.Path);
        Assert.Equal("main", info.Repository?.BaseBranch);
        Assert.True(info.Repository?.IsDefault);
        Assert.DoesNotContain("stale/legacy-path", info.Repository?.Path);
        Assert.DoesNotContain("stale.example", info.Repository?.Remote);
        Assert.DoesNotContain("ancient-branch", info.Repository?.BaseBranch);
    }

    [Fact]
    public void Snapshot_RoundTrip_DropsRepositoryField_AndKeepsReference()
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue_rt",
            ProjectId = "proj_rt",
            Number = 1,
            Title = "roundtrip",
            Labels = [],
            Priority = "p2",
            RepositoryRef = "secondary",
        };
        issue.Status = IssueStatus.Backlog;

        var json = IssueStore.Serialize(issue);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.TryGetProperty("RepositoryRef", out var refElement));
            Assert.Equal("secondary", refElement.GetString());
            Assert.False(doc.RootElement.TryGetProperty("Repository", out _));
        }

        var reloaded = IssueStore.Deserialize(json);
        Assert.NotNull(reloaded);
        Assert.Equal("secondary", reloaded!.RepositoryRef);
    }

    [Fact]
    public async Task ReadModel_ReferencesUnknownRepository_SurfacesRepositoryNotFoundProblem()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var orphan = new Mohist.Server.Issue.Domain.Issue
            {
                Id = "issue_orphan",
                ProjectId = projectId,
                Number = 2,
                Title = "Orphan",
                Labels = [],
                Priority = "p2",
                RepositoryRef = "deleted-repo",
            };
            orphan.Status = IssueStatus.Backlog;
            db.Issues.Add(new IssueRow
            {
                IssueId = orphan.Id,
                State = IssueStore.Serialize(orphan),
            });
            await db.SaveChangesAsync();
        }

        var info = await GetIssueInfoAsync(projectId, 2);

        Assert.NotNull(info);
        Assert.Null(info!.Repository);
        Assert.NotNull(info.RepositoryProblem);
        Assert.Equal(IssueRepositoryProblemCode.RepositoryNotFound, info.RepositoryProblem!.Code);
        Assert.Equal("deleted-repo", info.RepositoryProblem.RepositoryRef);
        Assert.NotNull(info.RepositoryProblem.CandidateNames);
        Assert.Contains("main", info.RepositoryProblem.CandidateNames!);
        Assert.Contains("secondary", info.RepositoryProblem.CandidateNames!);
    }

    [Fact]
    public async Task ReadModel_ProjectWithNoRepositories_SurfacesProjectHasNoRepositoriesProblem()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", "/proj/empty", "main");
        await projectGrain.RemoveRepositoryAsync("main");

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var issue = new Mohist.Server.Issue.Domain.Issue
            {
                Id = "issue_norepos",
                ProjectId = projectId,
                Number = 1,
                Title = "No repos",
                Labels = [],
                Priority = "p2",
                RepositoryRef = "main",
            };
            issue.Status = IssueStatus.Backlog;
            db.Issues.Add(new IssueRow
            {
                IssueId = issue.Id,
                State = IssueStore.Serialize(issue),
            });
            await db.SaveChangesAsync();
        }

        var info = await GetIssueInfoAsync(projectId, 1);

        Assert.NotNull(info);
        Assert.Null(info!.Repository);
        Assert.NotNull(info.RepositoryProblem);
        Assert.Equal(IssueRepositoryProblemCode.ProjectHasNoRepositories, info.RepositoryProblem!.Code);
    }

    [Fact]
    public async Task ReadModel_ProjectMissing_SurfacesProjectMissingProblem()
    {
        var info = await GetIssueInfoAsync("proj_nonexistent", 1);

        Assert.Null(info);
    }

    private async Task<(string ProjectId, ProjectInfo Project)> SetupProjectWithRepositoriesAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        var project = await grain.CreateAsync($"proj-{Guid.NewGuid():N}", "/proj/main", "main");
        await grain.AddRepositoryAsync("secondary", "/proj/secondary", "git@secondary.example:repo.git", "develop");
        return (projectId, project);
    }

    private async Task<string> LoadStateAsync(string projectId, int number)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Issues.AsNoTracking().FirstAsync(r => r.ProjectId == projectId && r.Number == number);
        return row.State;
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _fixture.Services.CreateScope();
        var projectsQuery = scope.ServiceProvider.GetRequiredService<ProjectQuerier>();
        var issuesQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var project = await projectsQuery.GetByIdAsync(projectId);
        return await issuesQuery.GetInfoAsync(projectId, number, project);
    }
}
