using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueWorkflowLifecycleSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly HttpClient _client;
    private readonly string _connectionString;

    public IssueWorkflowLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _client = fixture.Client;
        _connectionString = fixture.ConnectionString;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RerunAsync_WhenActiveWorkflowStateCannotDeserialize_ReplacesActiveWorkflow()
    {
        var (projectId, issueNumber, _, oldWrId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).StopAsync("test-stop");
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).DeactivateForTestAsync();
        await PoisonWorkflowFailureReasonAsync(oldWrId, "RemovedReason");

        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{issueNumber}/rerun");

        var restarted = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(restarted);
        Assert.Equal("in_progress", restarted!.Status);
        Assert.NotNull(restarted.WorkflowRunId);
        Assert.NotEqual(oldWrId, restarted.WorkflowRunId);

        var newRun = await LoadWorkflowRunAsync(restarted.WorkflowRunId!);
        Assert.NotNull(newRun);
        Assert.Equal(WorkflowRunStatus.Running, newRun!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_WhenExistingWorkflowIsStopped_StartsNewWorkflow()
    {
        var (projectId, issueNumber, issueId, oldWrId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).StopAsync("test-stop");

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        var newWrId = await issue.StartWorkAsync();

        Assert.NotEqual(oldWrId, newWrId);
        var restarted = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(restarted);
        Assert.Equal("in_progress", restarted!.Status);
        Assert.Equal(newWrId, restarted.WorkflowRunId);
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

    private async Task PoisonWorkflowFailureReasonAsync(string workflowRunId, string reason)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run {workflowRunId} was not stored");
        using var doc = JsonDocument.Parse(row.State);
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText())!;
        state["status"] = JsonSerializer.SerializeToElement("Failed", JSON.Options);
        state["failure"] = JsonSerializer.SerializeToElement(new
        {
            reason,
            stageId = "plan",
            message = "removed failure reason",
        }, JSON.Options);
        row.State = JsonSerializer.Serialize(state, JSON.Options);
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRun?> LoadWorkflowRunAsync(string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        return row is null ? null : JSON.Deserialize<WorkflowRun>(row.State);
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetInfoAsync(projectId, number);
    }
}
