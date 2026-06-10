using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueWorkflowLifecycleSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public IssueWorkflowLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompleteWorkAsync_IssueTransitionsFromInProgressToDone()
    {
        var (projectId, issueNumber, issueId, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await issue.CompleteWorkAsync(wrId);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("done", final!.Status);
        Assert.Equal(wrId, final.WorkflowRunId);

        await issue.ArchiveAsync();
        var archived = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.Null(archived!.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CancelAsync_WhenWorkflowRunning_RejectsWithError()
    {
        var (_, _, issueId, _) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CancelAsync_WhenWorkflowStopped_IssueTransitionsToCancelled()
    {
        var (projectId, issueNumber, issueId, wrId) = await SeedIssueInProgressAsync();

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("user-stopped");

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await issue.CancelAsync();

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("cancelled", final!.Status);
        Assert.Null(final.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompleteWorkAsync_ForIssueNotInProgress_StaysInCurrentState()
    {
        var projectId = await SeedProjectAsync();
        var (issueId, issueNumber) = await CreateIssueInBacklogAsync(projectId);

        var wrId = $"wr_{Guid.NewGuid():N}";
        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await issue.CompleteWorkAsync(wrId);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("backlog", final!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompleteWorkAsync_ForUnknownIssue_NoGrainThrows()
    {
        var issueId = $"issue_{Guid.NewGuid():N}";
        var wrId = $"wr_{Guid.NewGuid():N}";
        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await issue.CompleteWorkAsync(wrId);
    }

    private async Task<string> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", "/tmp/mohist-lifecycle", null);
        return id;
    }

    private async Task<(string issueId, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, number, "Lifecycle", null, null, null, null, issueId);
        return (issueId, number);
    }

    private async Task<(string projectId, int number, string issueId, string wrId)> SeedIssueInProgressAsync()
    {
        var projectId = await SeedProjectAsync();
        var (issueId, number) = await CreateIssueInBacklogAsync(projectId);

        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(
            Id: projectId,
            Name: $"proj-{Guid.NewGuid():N}",
            Path: "/tmp/mohist-lifecycle",
            BaseBranch: "main"));

        return (projectId, number, issueId, wrId);
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetInfoAsync(projectId, number);
    }
}
