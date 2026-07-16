using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue3")]
public class IssueWorkflowRepositoryResolutionSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public IssueWorkflowRepositoryResolutionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_ResolvesRepositoryFromCurrentProjectConfig_AndDispatchesRepositoryVariables()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "placeholder", GitUrl = "git@example.com:placeholder.git", BaseBranch = "main", IsDefault = true });
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo.git",
            "develop");

        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        await issueGrain.CreateAsync(projectId, number, "Use secondary repo", body: null, labels: null, priority: null, "secondary");

        var wrId = await issueGrain.StartWorkAsync();

        var variables = await LoadWorkflowVariablesAsync(wrId);
        var repository = variables.RootElement.GetProperty("repository");
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:repo.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("develop", repository.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_AfterProjectRepositoryConfigChange_UsesLatestRepositoryMetadata()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "placeholder", GitUrl = "git@example.com:placeholder.git", BaseBranch = "main", IsDefault = true });
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-old.git",
            "develop");

        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        await issueGrain.CreateAsync(projectId, number, "Repo metadata drifts", body: null, labels: null, priority: null, "secondary");

        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release");

        var wrId = await issueGrain.StartWorkAsync();

        var variables = await LoadWorkflowVariablesAsync(wrId);
        var repository = variables.RootElement.GetProperty("repository");
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:repo-new.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("release", repository.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_ReferencedRepositoryRemovedAfterIssueCreation_ThrowsRepositoryConfigurationProblem()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "placeholder", GitUrl = "git@example.com:placeholder.git", BaseBranch = "main", IsDefault = true });
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo.git",
            "develop");
        await projectGrain.AddRepositoryAsync(
            "main",
            "git@main.example:repo.git",
            "main");

        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        await issueGrain.CreateAsync(projectId, number, "Repo gets removed", body: null, labels: null, priority: null, "secondary");

        await projectGrain.RemoveRepositoryAsync("secondary");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issueGrain.StartWorkAsync());
        Assert.Contains("secondary", ex.Message);
        Assert.Contains("RepositoryNotFound", ex.Message);

        using var scope = _services.CreateScope();
        var issueQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var info = await issueQuery.GetInfoAsync(projectId, number, await LoadProjectAsync(projectId));
        Assert.NotNull(info);
        Assert.Null(info.WorkflowRunId);
        Assert.Null(info.Repository);
        Assert.NotNull(info.RepositoryProblem);
        Assert.Equal(IssueRepositoryProblemCode.RepositoryNotFound, info.RepositoryProblem!.Code);
        Assert.Equal("secondary", info.RepositoryProblem.RepositoryRef);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_ResolutionFailure_DoesNotCreateWorkflowOrDispatchWork()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo { Name = "placeholder", GitUrl = "git@example.com:placeholder.git", BaseBranch = "main", IsDefault = true });
        await projectGrain.AddRepositoryAsync("main", "git@main.example:repo.git", "main");

        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        await issueGrain.CreateAsync(projectId, number, "Ghost repo", body: null, labels: null, priority: null, "ghost");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issueGrain.StartWorkAsync());
        Assert.Contains("RepositoryNotFound", ex.Message);

        using var scope = _services.CreateScope();
        var issueQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var info = await issueQuery.GetInfoAsync(projectId, number, await LoadProjectAsync(projectId));
        Assert.NotNull(info);
        Assert.Null(info!.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_ExistingIssueWithoutRepositorySelection_UsesUpgradedDefaultRepository()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync(
            $"proj-{Guid.NewGuid():N}",
            new Mohist.Server.Project.Domain.RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "release",
                IsDefault = true,
            });
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        await issueGrain.CreateAsync(projectId, number, "Existing issue", body: null, labels: null, priority: null, repositoryRef: null);
        var issueBeforeUpgrade = await LoadIssueStateAsync(projectId, number);

        await projectGrain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var project = await db.Projects.SingleAsync(row => row.Id == projectId);
            project.RepositoriesJson = project.RepositoriesJson.Replace("\"isDefault\":true", "\"isDefault\":false", StringComparison.Ordinal);
            await db.SaveChangesAsync();
            await ProjectRepositoryDataUpgrader.UpgradeAsync(db);
        }

        var wrId = await issueGrain.StartWorkAsync();

        using var variables = await LoadWorkflowVariablesAsync(wrId);
        var repository = variables.RootElement.GetProperty("repository");
        Assert.Equal("server", repository.GetProperty("name").GetString());
        Assert.Equal("git@example.com:server.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("release", repository.GetProperty("baseBranch").GetString());
        var issueAfterUpgrade = await LoadIssueStateAsync(projectId, number);
        Assert.Equal(issueBeforeUpgrade.GetProperty("projectId").GetString(), issueAfterUpgrade.GetProperty("projectId").GetString());
        Assert.Equal(issueBeforeUpgrade.GetProperty("number").GetInt32(), issueAfterUpgrade.GetProperty("number").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task UpgradeAsync_InFlightWorkflowRetainsRepositoryVariables()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync(
            $"proj-{Guid.NewGuid():N}",
            new Mohist.Server.Project.Domain.RepositoryInfo
            {
                Name = "server",
                GitUrl = "git@example.com:server.git",
                BaseBranch = "release",
                IsDefault = true,
            });
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        await issueGrain.CreateAsync(projectId, number, "In-flight issue", body: null, labels: null, priority: null, repositoryRef: null);
        var workflowRunId = await issueGrain.StartWorkAsync();
        using var variablesBeforeUpgrade = await LoadWorkflowVariablesAsync(workflowRunId);
        var statusBeforeUpgrade = (await issueGrain.GetWorkflowStatusAsync())!;

        await projectGrain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var project = await db.Projects.SingleAsync(row => row.Id == projectId);
            project.RepositoriesJson = JsonSerializer.Serialize(
                new[]
                {
                    new Mohist.Server.Project.Domain.RepositoryInfo
                    {
                        Name = "server",
                        GitUrl = "git@example.com:server.git",
                        BaseBranch = "release",
                        IsDefault = false,
                    },
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.SaveChangesAsync();
            await ProjectRepositoryDataUpgrader.UpgradeAsync(db);

            db.ChangeTracker.Clear();
            var upgradedProject = await db.Projects.SingleAsync(row => row.Id == projectId);
            var repositories = JsonSerializer.Deserialize<List<Mohist.Server.Project.Domain.RepositoryInfo>>(
                upgradedProject.RepositoriesJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var repository = Assert.Single(repositories);
            Assert.True(repository.IsDefault);
        }

        using var variablesAfterUpgrade = await LoadWorkflowVariablesAsync(workflowRunId);
        Assert.Equal(
            variablesBeforeUpgrade.RootElement.GetProperty("repository").GetRawText(),
            variablesAfterUpgrade.RootElement.GetProperty("repository").GetRawText());
        var statusAfterUpgrade = (await issueGrain.GetWorkflowStatusAsync())!;
        Assert.Equal(statusBeforeUpgrade.Stage, statusAfterUpgrade.Stage);
        Assert.Equal(statusBeforeUpgrade.RuntimeStatus, statusAfterUpgrade.RuntimeStatus);
        Assert.Equal(statusBeforeUpgrade.WorkflowRunId, statusAfterUpgrade.WorkflowRunId);
        Assert.Equal(workflowRunId, await issueGrain.StartWorkAsync());
    }

    private async Task<JsonElement> LoadIssueStateAsync(string projectId, int issueNumber)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var state = await db.Issues
            .Where(issue => issue.ProjectId == projectId && issue.Number == issueNumber)
            .Select(issue => issue.State)
            .SingleAsync();
        return JsonDocument.Parse(state).RootElement.Clone();
    }

    private async Task<ProjectInfo> LoadProjectAsync(string projectId)
    {
        return (await _grains.GetGrain<IProjectGrain>(projectId).GetAsync())!;
    }

    private async Task<JsonDocument> LoadWorkflowVariablesAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        var snapshot = await query.GetEffectiveVariablesAsync(workflowRunId);
        return JsonDocument.Parse(snapshot.GetRawText());
    }
}
