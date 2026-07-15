using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
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
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.CreateAsync(projectId, number, "Use secondary repo", body: null, labels: null, priority: null, "secondary", issueId);

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
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.CreateAsync(projectId, number, "Repo metadata drifts", body: null, labels: null, priority: null, "secondary", issueId);

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
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.CreateAsync(projectId, number, "Repo gets removed", body: null, labels: null, priority: null, "secondary", issueId);

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
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
        await issueGrain.CreateAsync(projectId, number, "Ghost repo", body: null, labels: null, priority: null, "ghost", issueId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issueGrain.StartWorkAsync());
        Assert.Contains("RepositoryNotFound", ex.Message);

        using var scope = _services.CreateScope();
        var issueQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var info = await issueQuery.GetInfoAsync(projectId, number, await LoadProjectAsync(projectId));
        Assert.NotNull(info);
        Assert.Null(info!.WorkflowRunId);
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
