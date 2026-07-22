using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

/// <summary>
/// issue-417 T-006 (D4): covers the immutable run repository
/// context and the input-idempotent <c>EnsureStarted</c> behavior.
/// Acceptance scenarios:
/// <list type="bullet">
///   <item>A fresh run assigns the supplied repository context,
///     transitions to Pending, and emits Started + StageStarted once.</item>
///   <item>A duplicate replay with identical input succeeds and
///     emits no events.</item>
///   <item>A duplicate replay with conflicting context throws
///     <see cref="InvalidOperationException"/>.</item>
///   <item>Generic (non-Issue-backed) starts may pass a null
///     repository context; the run stills starts normally.</item>
///   <item>The run state round-trips through serialization
///     preserving the immutable repository context.</item>
/// </list>
/// </summary>
public class WorkflowRunRepositoryContextTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static WorkflowRepositoryContext SampleRepository(string name = "web") =>
        new(
            Name: name,
            GitUrl: $"git@{name}.example:repo.git",
            BaseBranch: "develop");

    private static WorkspaceIdentity SampleWorkspace(string wrId = "wr_xyz") =>
        new(
            Path: $"/run/workspaces/{wrId}",
            Branch: $"mohist/run-{wrId}",
            ChangeDir: "/openspec/changes/issue-417");

    private static WorkflowRun NewRun()
    {
        var run = WorkflowRun.Create(
            "wr_test",
            new WorkflowDefinition( [
                new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
            ]),
            FixedNow);
        return run;
    }

    [Fact]
    public void EnsureStarted_FreshRun_AssignsRepositoryContextAndEmitsStartedEventsOnce()
    {
        var run = NewRun();
        var repository = SampleRepository();
        var workspace = SampleWorkspace(run.Id);

        var events = run.EnsureStarted(repository, workspace, FixedNow);

        Assert.Equal(2, events.Count);
        Assert.True(events[0] is WorkflowRunStarted,
            "First event must be WorkflowRunStarted");
        Assert.True(events[1] is StageStarted started && started.Stage == "plan",
            "Second event must be StageStarted for the first stage");

        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        Assert.NotNull(run.Repository);
        Assert.Equal("web", run.Repository!.Name);
        Assert.Equal("develop", run.Repository.BaseBranch);
        Assert.NotNull(run.Workspace);
        Assert.Equal(workspace.Path, run.Workspace!.Path);
    }

    [Fact]
    public void EnsureStarted_DuplicateReplayWithIdenticalInput_IsNoOp()
    {
        var run = NewRun();
        var repository = SampleRepository();
        var workspace = SampleWorkspace(run.Id);

        var first = run.EnsureStarted(repository, workspace, FixedNow);
        Assert.Equal(2, first.Count);

        var replay = run.EnsureStarted(repository, workspace, FixedNow);

        // The idempotent replay emits no new events; the run keeps
        // its original repository snapshot.
        Assert.Empty(replay);
        Assert.NotNull(run.Repository);
        Assert.Equal("web", run.Repository!.Name);
    }

    [Fact]
    public void EnsureStarted_DuplicateReplayWithConflictingRepository_Throws()
    {
        var run = NewRun();
        var first = SampleRepository("web");
        run.EnsureStarted(first, SampleWorkspace(run.Id), FixedNow);

        var conflicting = SampleRepository("server");

        Assert.Throws<InvalidOperationException>(
            () => run.EnsureStarted(conflicting, SampleWorkspace(run.Id), FixedNow));
    }

    [Fact]
    public void EnsureStarted_DuplicateReplayWithConflictingIssueContext_Throws()
    {
        var run = NewRun();
        var repository = SampleRepository();
        var workspace = SampleWorkspace(run.Id);
        var firstContext = new WorkflowRunMetadata(
            null,
            FixedNow,
            Annotations: new Dictionary<string, string>
            {
                ["projectId"] = "proj_a",
                ["issueId"] = "issue_a",
                ["issueNumber"] = "1",
            });
        run.EnsureStarted(repository, workspace, FixedNow, firstContext);

        var conflictingContext = firstContext with
        {
            Annotations = new Dictionary<string, string>
            {
                ["projectId"] = "proj_a",
                ["issueId"] = "issue_b",
                ["issueNumber"] = "1",
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => run.EnsureStarted(repository, workspace, FixedNow, conflictingContext));
    }

    [Fact]
    public void EnsureStarted_GenericRunWithoutRepository_StartsWithoutContext()
    {
        var run = NewRun();

        var events = run.EnsureStarted(null, null, FixedNow);

        Assert.Equal(2, events.Count);
        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        Assert.Null(run.Repository);
        Assert.Null(run.Workspace);
    }

    [Fact]
    public void EnsureStarted_DuplicateReplayWithConflictingWorkspaceIdentity_Throws()
    {
        var run = NewRun();
        var repository = SampleRepository();
        var firstWorkspace = SampleWorkspace(run.Id);
        run.EnsureStarted(repository, firstWorkspace, FixedNow);

        var conflictingWorkspace = new WorkspaceIdentity(
            Path: "/run/workspaces/different",
            Branch: firstWorkspace.Branch,
            ChangeDir: firstWorkspace.ChangeDir);

        Assert.Throws<InvalidOperationException>(
            () => run.EnsureStarted(repository, conflictingWorkspace, FixedNow));
    }

    [Fact]
    public void EnsureStarted_DuplicateReplayWithDifferentWorkspacePath_Throws()
    {
        var run = NewRun();
        var repository = SampleRepository();
        var firstWorkspace = new WorkspaceIdentity(
            Path: "/run/workspaces/first",
            Branch: "mohist/run-wr_test",
            ChangeDir: "/c");
        run.EnsureStarted(repository, firstWorkspace, FixedNow);

        var differentPathWorkspace = new WorkspaceIdentity(
            Path: "/run/workspaces/second",
            Branch: "mohist/run-wr_test",
            ChangeDir: "/c");

        Assert.Throws<InvalidOperationException>(
            () => run.EnsureStarted(repository, differentPathWorkspace, FixedNow));
    }

    [Fact]
    public void Run_StateRoundTrip_PreservesRepositoryContext()
    {
        var run = NewRun();
        var repository = SampleRepository("web");
        run.EnsureStarted(repository, SampleWorkspace(run.Id), FixedNow);

        var json = JsonSerializer.Serialize(run, JSON.Options);
        var reloaded = JsonSerializer.Deserialize<WorkflowRun>(json, JSON.Options);

        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.Repository);
        Assert.Equal(repository.Name, reloaded.Repository!.Name);
        Assert.Equal(repository.GitUrl, reloaded.Repository.GitUrl);
        Assert.Equal(repository.BaseBranch, reloaded.Repository.BaseBranch);
    }

    [Fact]
    public void Start_AfterEnsureStarted_DoesNotOverwriteRepositoryContext()
    {
        // Contract: once the run has been ensured-started, the
        // legacy Start path (carrying no snapshot) cannot erase
        // the persisted repository context. Start() throws
        // because the run is no longer in Created status.
        var run = NewRun();
        var repository = SampleRepository();
        run.EnsureStarted(repository, SampleWorkspace(run.Id), FixedNow);

        Assert.Throws<InvalidOperationException>(
            () => run.Start(FixedNow));
    }
}
