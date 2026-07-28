using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Events.Grains;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IssueLifecycle")]
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

    [Fact]
    public async Task CompleteWorkAsync_IssueTransitionsFromInProgressToDone()
    {
        var (projectId, _, issueNumber, issueKey, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.CompleteWorkAsync(wrId);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("done", final!.Status);
        Assert.Equal(wrId, final.WorkflowRunId);

        await issue.ArchiveAsync();
        var archived = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(archived);
        Assert.Equal(wrId, archived!.WorkflowRunId);
        Assert.NotNull(archived.ArchivedAt);
    }

    [Fact]
    public async Task CancelAsync_WhenWorkflowRunning_RejectsWithError()
    {
        var (_, _, _, issueKey, _) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());
    }

    [Fact]
    public async Task CancelAsync_WhenWorkflowStopped_IssueTransitionsToCancelled()
    {
        var (projectId, _, issueNumber, issueKey, wrId) = await SeedIssueInProgressAsync();

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("user-stopped");

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.CancelAsync();

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("cancelled", final!.Status);
        Assert.Equal(wrId, final.WorkflowRunId);
    }

    [Fact]
    public async Task CancelAsync_WhenIssueIsDoneWithPreservedReference_RejectsViaCloseOnly()
    {
        // A Done issue preserves its workflowRunId as an execution fact.
        // CancelAsync must not run the "cannot close while workflow is
        // running" check (the workflow is not active); Close() itself
        // rejects Done/archived with a different error. Verify the thrown
        // exception is the Close() rejection, not the workflow-running one.
        var (_, _, _, issueKey, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.CompleteWorkAsync(wrId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());
        Assert.Contains("cannot close", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflow is", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateFullAsync_WhenWorkflowHasStarted_ChangesIssueSelectionWithoutChangingRunBinding()
    {
        var (projectId, _, issueNumber, issueKey, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.UpdateFullAsync(new UpdateIssueData(
            WorkflowProfileId: "mohist/github-pr",
            PresentFields: new HashSet<string>(StringComparer.Ordinal) { nameof(UpdateIssueData.WorkflowProfileId) }));

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.Equal("mohist/github-pr", final!.WorkflowProfileId);
        Assert.Equal(wrId, final.WorkflowRunId);

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.Equal("mohist/local", run!.WorkflowProfileId);
    }

    [Fact]
    public async Task CompleteWorkAsync_ForIssueNotInProgress_StaysInCurrentState()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);

        var wrId = $"wr_{Guid.NewGuid():N}";
        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.CompleteWorkAsync(wrId);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("backlog", final!.Status);
    }

    [Fact]
    public async Task RerunAsync_WhenFailureReasonIsUnknownLegacyValue_RerunsExistingWorkflow()
    {
        var (projectId, _, issueNumber, _, oldWrId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).StopAsync("test-stop");
        await TestLifecycle.Deactivate(_grains.GetGrain<IWorkflowGrain>(oldWrId));
        await PoisonWorkflowFailureReasonAsync(oldWrId, "RemovedReason");

        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{issueNumber}/rerun");

        var restarted = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(restarted);
        Assert.Equal("in_progress", restarted!.Status);
        Assert.Equal(oldWrId, restarted.WorkflowRunId);

        var run = await LoadWorkflowRunAsync(oldWrId);
        Assert.NotNull(run);
        // After Rerun, the stage is reset to running. With no assignment
        // on the run (the prior StopAsync left it unassigned because
        // no runner ever picked it up), the new state machine lands on
        // Pending (started, has dispatchable work, waiting for any
        // runner to claim it).
        Assert.Equal(WorkflowRunStatus.Pending, run!.Status);
    }

    [Fact]
    public async Task StartWorkAsync_WhenExistingWorkflowIsStopped_StartsNewWorkflow()
    {
        var (projectId, _, issueNumber, issueKey, oldWrId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).StopAsync("test-stop");

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        var newWrId = await issue.StartWorkAsync();

        Assert.NotEqual(oldWrId, newWrId);
        var restarted = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(restarted);
        Assert.Equal("in_progress", restarted!.Status);
        Assert.Equal(newWrId, restarted.WorkflowRunId);
    }

    [Fact]
    public async Task StartWorkAsync_PersistsWorkspaceIdentityOnWorkflowRun()
    {
        var (projectId, projectName, issueNumber, issueKey, wrId) = await SeedIssueInProgressAsync();

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        Assert.NotNull(run!.Workspace);
        Assert.Equal(
            MohistWorkspaceLayout.WorkflowRunWorkspacePath(_fixture.RunnerRoot, wrId),
            run.Workspace.Path);
        Assert.Equal($"mohist/run-{wrId}", run.Workspace.Branch);
        Assert.Equal($"openspec/changes/issue-{issueNumber}", run.Workspace.ChangeDir);

        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var started = (await events.ListIssueEventsAsync(projectId, issueNumber, 100))
            .LastOrDefault(e => string.Equals(e.Envelope.Type, EventCatalog.ReverseDns.IssueWorkStarted, StringComparison.Ordinal));
        Assert.NotNull(started);
        var payload = started!.Envelope.Data!.Value.Deserialize<IssueWorkStarted>(JSON.Options);
        Assert.NotNull(payload);
        Assert.Equal(run.Repository!.Name, payload!.Repository!.Name);
        Assert.Equal(run.Workspace.Path, payload.Workspace!.Path);
        Assert.Equal(issueNumber, payload.Context!.IssueNumber);
    }

    [Fact]
    public async Task StartWorkAsync_WhenActiveRunExists_ReusesRunAndWorkspace()
    {
        var (_, _, _, issueKey, firstWrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        var secondWrId = await issue.StartWorkAsync();

        Assert.Equal(firstWrId, secondWrId);

        var run = await LoadWorkflowRunAsync(firstWrId);
        Assert.NotNull(run);
        Assert.NotNull(run!.Workspace);
    }

    [Fact]
    public async Task EventDispatcher_RedeliversIssueWorkStartedAndCreatesMissingWorkflowRun()
    {
        var (projectId, _, issueNumber, _, workflowRunId) = await SeedIssueInProgressAsync();
        await TestLifecycle.Deactivate(_grains.GetGrain<IWorkflowGrain>(workflowRunId));
        await _grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);
        await DeleteWorkflowRunAsync(workflowRunId);
        await MarkIssueWorkStartedUndeliveredAsync(projectId, issueNumber);

        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<EventDispatcherService>();
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Pending, (await LoadWorkflowRunAsync(workflowRunId))!.Status);
        await AssertIssueWorkStartedDispatchedAsync(projectId, issueNumber);
    }

    [Fact]
    public async Task StartWorkAsync_WhenPrerequisiteIncomplete_DoesNotCreateWorkflowRunOrWorkspace()
    {
        var (projectId, _) = await SeedProjectAsync();
        var prereq = await CreateIssueInBacklogAsync(projectId);
        var dependent = await CreateIssueInBacklogAsync(projectId);

        var dependentGrain = _grains.GetGrain<IIssueGrain>(dependent.issueKey);
        await dependentGrain.AddPrerequisiteAsync(prereq.number);

        await Assert.ThrowsAsync<IssueStartBlockedException>(() => dependentGrain.StartWorkAsync());

        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options);

        var runsForDependent = db.WorkflowRuns
            .AsNoTracking()
            .Where(r => r.MetadataProjectId == projectId)
            .ToList();
        Assert.Empty(runsForDependent);

        var projectRow = await db.Projects.FindAsync(projectId);
        Assert.NotNull(projectRow);
        Assert.DoesNotContain("path", projectRow!.RepositoriesJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path", projectRow.RepositoriesJson);
    }

    [Fact]
    public async Task ProjectAndRepository_AfterStart_HaveNoLocalPathFields()
    {
        var (projectId, _, _, _, wrId) = await SeedIssueInProgressAsync();

        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options);

        var projectRow = await db.Projects.FindAsync(projectId);
        Assert.NotNull(projectRow);
        Assert.DoesNotContain("Path", projectRow!.RepositoriesJson);
        Assert.DoesNotContain("ResolvedPath", projectRow.RepositoriesJson);

        var runRow = await db.WorkflowRuns.FindAsync(wrId);
        Assert.NotNull(runRow);
        Assert.Contains("\"workspace\"", runRow!.State);
        Assert.Contains("\"path\"", runRow.State);
    }

    [Fact]
    public async Task StartWork_DispatchVariables_ExposeWorkspacePathAndRepositoryMetadataOnly()
    {
         var (_, _, _, _, wrId) = await SeedIssueInProgressAsync();

        using var scope = _services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        var snapshot = await query.GetEffectiveVariablesAsync(wrId);

        using var doc = JsonDocument.Parse(snapshot.GetRawText());
        var root = doc.RootElement;

         Assert.False(root.TryGetProperty("mohist", out _));
         Assert.False(root.TryGetProperty("project", out _));
         Assert.False(root.TryGetProperty("repository", out _));
         Assert.False(root.TryGetProperty("workspace", out _));
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"mohist-local-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name, new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist-local.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return (id, name);
    }

    private async Task<(string issueKey, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, "Lifecycle", null, null, null, isDraft: false);
        return (issueKey, number);
    }

    private async Task<(string projectId, string projectName, int number, string issueKey, string wrId)> SeedIssueInProgressAsync()
    {
        var (projectId, projectName) = await SeedProjectAsync();
        var (issueKey, number) = await CreateIssueInBacklogAsync(projectId);

        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();

        return (projectId, projectName, number, issueKey, wrId);
    }

    private async Task DispatchEventsAsync()
    {
        await _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
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

    private async Task DeleteWorkflowRunAsync(string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run {workflowRunId} was not stored");
        db.WorkflowRuns.Remove(row);
        await db.SaveChangesAsync();
    }

    private async Task MarkIssueWorkStartedUndeliveredAsync(string projectId, int issueNumber)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.IssueEvents
            .Where(entry => entry.Source == IssueEventPersistence.IssueSource(projectId, issueNumber)
                && entry.Type == EventCatalog.ReverseDns.IssueWorkStarted)
            .OrderByDescending(entry => entry.Id)
            .FirstAsync();
        row.DispatchedAt = null;
        await db.SaveChangesAsync();
    }

    private async Task AssertIssueWorkStartedDispatchedAsync(string projectId, int issueNumber)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.IssueEvents
            .AsNoTracking()
            .Where(entry => entry.Source == IssueEventPersistence.IssueSource(projectId, issueNumber)
                && entry.Type == EventCatalog.ReverseDns.IssueWorkStarted)
            .OrderByDescending(entry => entry.Id)
            .FirstAsync();
        Assert.NotNull(row.DispatchedAt);
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetInfoAsync(projectId, number);
    }
}
