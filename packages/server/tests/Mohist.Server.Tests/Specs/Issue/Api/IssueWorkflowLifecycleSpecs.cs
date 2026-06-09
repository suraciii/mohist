using CloudNative.CloudEvents;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueWorkflowLifecycleSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly IEventBus _eventBus;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public IssueWorkflowLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _eventBus = fixture.EventBus;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowRunCompleted_IssueTransitionsFromInProgressToDone()
    {
        var (projectId, issueNumber, wrId) = await SeedIssueInProgressAsync();

        var evt = BuildWorkflowEvent(EventCatalog.ReverseDns.WorkflowRunCompleted, projectId, issueNumber, wrId);
        await _eventBus.EmitAsync(evt);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("done", final!.Status);
        Assert.Null(final.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowRunStopped_IssueTransitionsFromInProgressToCancelled()
    {
        var (projectId, issueNumber, wrId) = await SeedIssueInProgressAsync();

        var evt = BuildWorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunStopped,
            projectId,
            issueNumber,
            wrId,
            reason: "user-stopped");
        await _eventBus.EmitAsync(evt);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("cancelled", final!.Status);
        Assert.Null(final.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowRunFailed_IssueTransitionsFromInProgressToCancelled()
    {
        var (projectId, issueNumber, wrId) = await SeedIssueInProgressAsync();

        var evt = BuildWorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunFailed,
            projectId,
            issueNumber,
            wrId,
            reason: "task-failed:build-1");
        await _eventBus.EmitAsync(evt);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("cancelled", final!.Status);
        Assert.Null(final.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowRunCompleted_ForIssueNotInProgress_StaysInCurrentState()
    {
        var projectId = await SeedProjectAsync();
        var (issueId, issueNumber) = await CreateIssueInBacklogAsync(projectId);

        var wrId = $"wr_{Guid.NewGuid():N}";
        var evt = BuildWorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunCompleted,
            projectId,
            issueNumber,
            wrId);
        await _eventBus.EmitAsync(evt);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("backlog", final!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowRunCompleted_ForUnknownIssue_NoGrainThrows()
    {
        var projectId = await SeedProjectAsync();
        var wrId = $"wr_{Guid.NewGuid():N}";
        var evt = BuildWorkflowEvent(
            EventCatalog.ReverseDns.WorkflowRunCompleted,
            projectId,
            issueNumber: 99999,
            wrId);
        await _eventBus.EmitAsync(evt);
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

    private async Task<(string projectId, int number, string wrId)> SeedIssueInProgressAsync()
    {
        var projectId = await SeedProjectAsync();
        var (issueId, number) = await CreateIssueInBacklogAsync(projectId);

        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(
            Id: projectId,
            Name: $"proj-{Guid.NewGuid():N}",
            Path: "/tmp/mohist-lifecycle",
            BaseBranch: "main"));

        return (projectId, number, wrId);
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetInfoAsync(projectId, number);
    }

    private static CloudEvent BuildWorkflowEvent(
        string type,
        string projectId,
        int issueNumber,
        string wrId,
        string? reason = null)
    {
        var extra = new Dictionary<string, object?>
        {
            ["projectid"] = projectId,
            ["workflowrunid"] = wrId,
            ["issueno"] = issueNumber.ToString(),
        };
        if (reason is not null) extra["reason"] = reason;
        return CloudEventFactory.Create(
            type: type,
            source: new Uri($"/mohist/workflow/{wrId}", UriKind.Relative),
            projectId: projectId,
            workflowRunId: wrId,
            issueNumber: issueNumber.ToString(),
            extraExtensions: extra);
    }
}
