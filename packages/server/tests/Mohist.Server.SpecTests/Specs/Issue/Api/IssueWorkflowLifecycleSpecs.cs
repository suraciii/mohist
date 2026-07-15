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
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue2")]
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
        var (projectId, _, issueNumber, issueId, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CancelAsync_WhenWorkflowRunning_RejectsWithError()
    {
        var (_, _, _, issueId, _) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CancelAsync_WhenWorkflowBindingIsPending_RejectsWithoutStrandingTheRun()
    {
        var (_, _, _, issueId, workflowRunId) = await SeedIssueInProgressAsync();
        await ResetBindingToPreparedAsync(issueId, workflowRunId);

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());

        Assert.Contains("awaiting-binding", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True((await LoadIssueBindingStateAsync(issueId)).Pending);
        Assert.Equal(WorkflowRunStatus.AwaitingBinding, (await LoadWorkflowRunAsync(workflowRunId))!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CancelAsync_AfterProtectedBindingCanBeStoppedCancelledReopenedAndRestarted()
    {
        var (projectId, _, issueNumber, issueId, originalWorkflowRunId) = await SeedIssueInProgressAsync();
        await ResetBindingToPreparedAsync(issueId, originalWorkflowRunId);

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());

        await issue.EnsureWorkflowBindingAsync(originalWorkflowRunId);
        await _grains.GetGrain<IWorkflowGrain>(originalWorkflowRunId).StopAsync("user-stopped");
        await issue.CancelAsync();
        await issue.ReopenAsync();

        var replacementWorkflowRunId = await issue.StartWorkAsync();

        Assert.NotEqual(originalWorkflowRunId, replacementWorkflowRunId);
        var restarted = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(restarted);
        Assert.Equal("in_progress", restarted!.Status);
        Assert.Equal(replacementWorkflowRunId, restarted.WorkflowRunId);
        Assert.False((await LoadIssueBindingStateAsync(issueId)).Pending);
        Assert.Equal(WorkflowRunStatus.Pending, (await LoadWorkflowRunAsync(replacementWorkflowRunId))!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CancelAsync_WhenWorkflowStopped_IssueTransitionsToCancelled()
    {
        var (projectId, _, issueNumber, issueId, wrId) = await SeedIssueInProgressAsync();

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("user-stopped");

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await issue.CancelAsync();

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("cancelled", final!.Status);
        Assert.Equal(wrId, final.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CancelAsync_WhenIssueIsDoneWithPreservedReference_RejectsViaCloseOnly()
    {
        // A Done issue preserves its workflowRunId as an execution fact.
        // CancelAsync must not run the "cannot close while workflow is
        // running" check (the workflow is not active); Close() itself
        // rejects Done/archived with a different error. Verify the thrown
        // exception is the Close() rejection, not the workflow-running one.
        var (_, _, _, issueId, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await issue.CompleteWorkAsync(wrId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());
        Assert.Contains("cannot close", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflow is", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task UpdateFullAsync_WhenWorkflowHasStarted_RejectsWorkflowProfileChange()
    {
        // The profile-lock guard keeps its "has started" semantics:
        // workflowRunId != null means the issue has bound a run, and the
        // template is locked from that point onward, including after
        // Done/archive. Verify the guard still rejects a profile change
        // when the reference is present, regardless of status.
        var (projectId, _, issueNumber, issueId, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        await issue.CompleteWorkAsync(wrId);

        await Assert.ThrowsAsync<WorkflowProfileLockedException>(() =>
            issue.UpdateFullAsync(new UpdateIssueData(
                WorkflowProfileId: "mohist/github-pr",
                PresentFields: new HashSet<string>(StringComparer.Ordinal) { nameof(UpdateIssueData.WorkflowProfileId) })));

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.Equal("done", final!.Status);
        Assert.Equal(wrId, final.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompleteWorkAsync_ForIssueNotInProgress_StaysInCurrentState()
    {
        var (projectId, _) = await SeedProjectAsync();
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
    public async Task RerunAsync_WhenFailureReasonIsUnknownLegacyValue_RerunsExistingWorkflow()
    {
        var (projectId, _, issueNumber, _, oldWrId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).StopAsync("test-stop");
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).DeactivateForTestAsync();
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_WhenExistingWorkflowIsStopped_StartsNewWorkflow()
    {
        var (projectId, _, issueNumber, issueId, oldWrId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).StopAsync("test-stop");

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        var newWrId = await issue.StartWorkAsync();

        Assert.NotEqual(oldWrId, newWrId);
        var restarted = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(restarted);
        Assert.Equal("in_progress", restarted!.Status);
        Assert.Equal(newWrId, restarted.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_PersistsWorkspaceIdentityOnWorkflowRun()
    {
        var (projectId, projectName, issueNumber, _, wrId) = await SeedIssueInProgressAsync();

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        Assert.NotNull(run!.Workspace);
        Assert.Equal(
            Path.Combine(_fixture.RunnerRoot, projectName, "workspaces", $"issue-{issueNumber}"),
            run.Workspace.Path);
        Assert.Equal($"mohist/run-{wrId}", run.Workspace.Branch);
        Assert.Equal($"openspec/changes/issue-{issueNumber}", run.Workspace.ChangeDir);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_WhenActiveRunExists_ReusesRunAndWorkspace()
    {
        var (_, _, _, issueId, firstWrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueId);
        var secondWrId = await issue.StartWorkAsync();

        Assert.Equal(firstWrId, secondWrId);

        var run = await LoadWorkflowRunAsync(firstWrId);
        Assert.NotNull(run);
        Assert.NotNull(run!.Workspace);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task EnsureWorkflowBindingAsync_RecreatesRunAfterIssueBindingWasCommitted()
    {
        var (_, _, _, issueId, workflowRunId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IIssueGrain>(issueId).DeactivateForTestAsync();
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId).DeactivateForTestAsync();
        await DeleteWorkflowRunAsync(workflowRunId);
        await MarkIssueBindingPendingAsync(issueId);
        await _grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        await _grains.GetGrain<IIssueGrain>(issueId).EnsureWorkflowBindingAsync(workflowRunId);

        var restored = await LoadWorkflowRunAsync(workflowRunId);
        Assert.NotNull(restored);
        Assert.Equal(WorkflowRunStatus.Pending, restored!.Status);
        Assert.True(restored.IssueLineageVersion > 0);

        var settled = await LoadIssueBindingStateAsync(issueId);
        Assert.False(settled.Pending);
        await _grains.GetGrain<IIssueGrain>(issueId).EnsureWorkflowBindingAsync(workflowRunId);
        Assert.Equal(settled, await LoadIssueBindingStateAsync(issueId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task EnsureWorkflowBindingAsync_ConfirmsPreparedRun()
    {
        var (_, _, _, issueId, workflowRunId) = await SeedIssueInProgressAsync();
        await ResetBindingToPreparedAsync(issueId, workflowRunId);

        await _grains.GetGrain<IIssueGrain>(issueId).EnsureWorkflowBindingAsync(workflowRunId);

        Assert.Equal(WorkflowRunStatus.Pending, (await LoadWorkflowRunAsync(workflowRunId))!.Status);
        Assert.False((await LoadIssueBindingStateAsync(issueId)).Pending);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task EventDispatcher_RedeliversInterruptedBindingAndConfirmsPreparedRun()
    {
        var (_, _, _, issueId, workflowRunId) = await SeedIssueInProgressAsync();
        await ResetBindingToPreparedAsync(issueId, workflowRunId);
        await MarkIssueWorkStartedUndeliveredAsync(issueId);

        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<EventDispatcherService>();
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(WorkflowRunStatus.Pending, (await LoadWorkflowRunAsync(workflowRunId))!.Status);
        Assert.False((await LoadIssueBindingStateAsync(issueId)).Pending);
        await AssertIssueWorkStartedDispatchedAsync(issueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task EnsureWorkflowBindingAsync_ClearsMarkerAfterWorkflowConfirmationCommitted()
    {
        var (_, _, _, issueId, workflowRunId) = await SeedIssueInProgressAsync();
        await ResetBindingToPreparedAsync(issueId, workflowRunId);
        var pending = await LoadIssueBindingStateAsync(issueId);

        await _grains.GetGrain<IWorkflowGrain>(workflowRunId).ConfirmIssueBindingAsync(
            new WorkflowIssueBinding(issueId, EpicId: null, pending.Version));

        Assert.Equal(WorkflowRunStatus.Pending, (await LoadWorkflowRunAsync(workflowRunId))!.Status);
        Assert.True((await LoadIssueBindingStateAsync(issueId)).Pending);

        await _grains.GetGrain<IIssueGrain>(issueId).EnsureWorkflowBindingAsync(workflowRunId);

        Assert.False((await LoadIssueBindingStateAsync(issueId)).Pending);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_WhenPrerequisiteIncomplete_DoesNotCreateWorkflowRunOrWorkspace()
    {
        var (projectId, _) = await SeedProjectAsync();
        var prereq = await CreateIssueInBacklogAsync(projectId);
        var dependent = await CreateIssueInBacklogAsync(projectId);

        var dependentGrain = _grains.GetGrain<IIssueGrain>(dependent.issueId);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWork_DispatchVariables_ExposeWorkspacePathAndRepositoryMetadataOnly()
    {
        var (projectId, projectName, _, _, wrId) = await SeedIssueInProgressAsync();

        using var scope = _services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        var snapshot = await query.GetEffectiveVariablesAsync(wrId);

        using var doc = JsonDocument.Parse(snapshot.GetRawText());
        var root = doc.RootElement;

        var project = root.GetProperty("project");
        Assert.Equal(projectId, project.GetProperty("id").GetString());
        Assert.Equal(projectName, project.GetProperty("name").GetString());
        Assert.False(project.TryGetProperty("path", out _));
        Assert.False(project.TryGetProperty("effectivePath", out _));

        var repository = root.GetProperty("repository");
        Assert.Equal("origin", repository.GetProperty("name").GetString());
        Assert.Equal("git@example.com:mohist-local.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("main", repository.GetProperty("baseBranch").GetString());
        Assert.False(repository.TryGetProperty("path", out _));
        Assert.False(repository.TryGetProperty("remote", out _));
        Assert.False(repository.TryGetProperty("resolvedPath", out _));

        var workspace = root.GetProperty("workspace");
        Assert.False(string.IsNullOrWhiteSpace(workspace.GetProperty("path").GetString()));
        var headBranch = workspace.GetProperty("branch").GetString();
        Assert.StartsWith("mohist/run-", headBranch);
        Assert.False(string.IsNullOrWhiteSpace(workspace.GetProperty("changeDir").GetString()));
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

    private async Task<(string issueId, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, number, "Lifecycle", null, null, null, null, issueId, isDraft: false);
        return (issueId, number);
    }

    private async Task<(string projectId, string projectName, int number, string issueId, string wrId)> SeedIssueInProgressAsync()
    {
        var (projectId, projectName) = await SeedProjectAsync();
        var (issueId, number) = await CreateIssueInBacklogAsync(projectId);

        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        var wrId = await grain.StartWorkAsync();

        return (projectId, projectName, number, issueId, wrId);
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

    private async Task ResetBindingToPreparedAsync(string issueId, string workflowRunId)
    {
        var original = await LoadWorkflowRunAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run {workflowRunId} was not stored");
        await _grains.GetGrain<IIssueGrain>(issueId).DeactivateForTestAsync();
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId).DeactivateForTestAsync();
        await DeleteWorkflowRunAsync(workflowRunId);
        await MarkIssueBindingPendingAsync(issueId);
        await _grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        await _grains.GetGrain<IWorkflowGrain>(workflowRunId).PrepareIssueStartAsync(
            new WorkflowStartInput(Metadata: original.Metadata, Workspace: original.Workspace));
        Assert.Equal(WorkflowRunStatus.AwaitingBinding, (await LoadWorkflowRunAsync(workflowRunId))!.Status);
    }

    private async Task<(bool Pending, long Version)> LoadIssueBindingStateAsync(string issueId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.Issues.AsNoTracking().SingleAsync(issue => issue.IssueId == issueId);
        var issue = IssueStore.Deserialize(row.State)
            ?? throw new InvalidOperationException($"Issue {issueId} state was not stored");
        return (issue.WorkflowBindingPending, row.LineageVersion);
    }

    private async Task MarkIssueBindingPendingAsync(string issueId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.Issues.SingleAsync(issue => issue.IssueId == issueId);
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.State, JSON.Options)
            ?? throw new InvalidOperationException($"Issue {issueId} state was not stored");
        state["workflowBindingPending"] = JsonSerializer.SerializeToElement(true, JSON.Options);
        row.State = JsonSerializer.Serialize(state, JSON.Options);
        await db.SaveChangesAsync();
    }

    private async Task MarkIssueWorkStartedUndeliveredAsync(string issueId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.IssueEvents
            .Where(entry => entry.Source == IssueEventPersistence.IssueSource(issueId)
                && entry.Type == EventCatalog.ReverseDns.IssueWorkStarted)
            .OrderByDescending(entry => entry.Id)
            .FirstAsync();
        row.DispatchedAt = null;
        await db.SaveChangesAsync();
    }

    private async Task AssertIssueWorkStartedDispatchedAsync(string issueId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.IssueEvents
            .AsNoTracking()
            .Where(entry => entry.Source == IssueEventPersistence.IssueSource(issueId)
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
