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

[Collection("IntegrationIssueRepository")]
public class IssueWorkflowRepositoryResolutionSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public IssueWorkflowRepositoryResolutionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Fact]
    public async Task StartWorkAsync_ResolvesRepositoryFromCurrentProjectConfig_AndDispatchesRepositoryVariables()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task StartWorkAsync_AfterProjectRepositoryConfigChange_UsesLatestRepositoryMetadata()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}");
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

    private async Task<JsonDocument> LoadWorkflowVariablesAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        var snapshot = await query.GetEffectiveVariablesAsync(workflowRunId);
        return JsonDocument.Parse(snapshot.GetRawText());
    }
}
