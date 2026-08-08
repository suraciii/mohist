using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

[Collection("WorkflowGrain")]
public sealed class IssueWorkflowLifecycleGrainSpecs
{
    private readonly WorkflowGrainFixture _fixture;
    private readonly IGrainFactory _grains;

    public IssueWorkflowLifecycleGrainSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
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
    public async Task RerunAsync_WhenFailureReasonIsUnknownLegacyValue_RerunsExistingWorkflow()
    {
        var (projectId, _, issueNumber, _, oldWrId) = await SeedIssueInProgressAsync();
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).StopAsync("test-stop");
        await TestLifecycle.Deactivate(_grains.GetGrain<IWorkflowGrain>(oldWrId));
        await PoisonWorkflowFailureReasonAsync(oldWrId, "RemovedReason");

        // Drive RerunAsync on the WorkflowGrain directly — the issue
        // route handler at /api/projects/{ref}/issues/{n}/rerun calls
        // IWorkflowGrain.RerunAsync on the issue's bound run, so the
        // grain-level call here is the surface the route relies on.
        await _grains.GetGrain<IWorkflowGrain>(oldWrId).RerunAsync();

        var restarted = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(restarted);
        Assert.Equal("in_progress", restarted!.Status);
        Assert.Equal(oldWrId, restarted.WorkflowRunId);

        var run = await LoadWorkflowRunAsync(oldWrId);
        Assert.NotNull(run);
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
        var (projectId, _, issueNumber, _, wrId) = await SeedIssueInProgressAsync();

        var run = await LoadWorkflowRunAsync(wrId);
        Assert.NotNull(run);
        Assert.NotNull(run!.Workspace);
        // The silo resolves the runner root via IEnvironmentVariableProvider
        // (MockEnvironmentVariableProvider on WorkflowGrainFixture, with no
        // env vars set); assert the workspace path the workflow run
        // stamped matches the layout helper's computation against that
        // root, without binding the spec to a host-specific path.
        using var envScope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var env = envScope.ServiceProvider.GetRequiredService<IEnvironmentVariableProvider>();
        var runnerRoot = MohistWorkspaceLayout.DefaultRunnerRoot(env);
        Assert.Equal(
            MohistWorkspaceLayout.WorkflowRunWorkspacePath(runnerRoot, wrId),
            run.Workspace.Path);
        Assert.Equal($"mohist/run-{wrId}", run.Workspace.Branch);
        Assert.Equal($"openspec/changes/issue-{issueNumber}", run.Workspace.ChangeDir);

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
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
    public async Task StartWorkAsync_RecordsIssueWorkStartedEnvelopeOnTheSharedEventStore()
    {
        // Counterpart to the redelivery test sunk into
        // IssueWorkflowLifecycleSpecs: WorkflowGrainFixture swaps the
        // production EventStore for the in-memory RecordingEventStore
        // (other workflow tests never touch IssueEvents), so we assert
        // here that IssueGrain.SaveIssueAsync still emits an
        // IssueWorkStarted envelope for the issue source when the
        // workflow run is started.
        var (projectId, _, issueNumber, _, _) = await SeedIssueInProgressAsync();

        var source = IssueEventPersistence.IssueSource(projectId, issueNumber);
        var recorded = _fixture.EventStore.Appended
            .Where(e => e.Envelope.Source.ToString() == source
                && e.Envelope.Type == EventCatalog.ReverseDns.IssueWorkStarted)
            .ToList();
        var envelope = Assert.Single(recorded);
        Assert.NotNull(envelope.Envelope.Data);
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
            .UseSqlite(_fixture.ConnectionString)
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
            .UseSqlite(_fixture.ConnectionString)
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

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
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
        await projectGrain.CreateAsync(name, new RepositoryInfo
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
        await _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

        return (projectId, projectName, number, issueKey, wrId);
    }

    private async Task PoisonWorkflowFailureReasonAsync(string workflowRunId, string reason)
    {
        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options);

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
        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options);

        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        return row is null ? null : JSON.Deserialize<WorkflowRun>(row.State);
    }

    private async Task DeleteWorkflowRunAsync(string workflowRunId)
    {
        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options);

        var row = await db.WorkflowRuns.FindAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run {workflowRunId} was not stored");
        db.WorkflowRuns.Remove(row);
        await db.SaveChangesAsync();
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetInfoAsync(projectId, number);
    }
}