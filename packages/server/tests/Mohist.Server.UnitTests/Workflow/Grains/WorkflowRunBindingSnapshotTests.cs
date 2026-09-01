using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grains;

public sealed class WorkflowRunBindingSnapshotTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BindAsync_PersistsDefinitionJson_OnRun()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var requested = CreateStart(BuildDefinitionJson());

        var applied = await participant.BindAsync(requested, "start-1", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, applied.Outcome);
        var stored = Assert.IsType<WorkflowRun>(await store.LoadAsync(requested.WorkflowRunId));
        Assert.Equal(requested.DefinitionJson, stored.BoundWorkflowDefinitionJson);
        Assert.Equal("npm run verify", stored.VerificationCommand);
    }

    [Fact]
    public async Task BindAsync_NullDefinitionJson_IsRejectedForNewRun()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            participant.BindAsync(CreateStart(null), "start-1", expectedRevision: null));
        Assert.Null(await store.LoadAsync("run-1"));
    }

    [Fact]
    public async Task BindAsync_InvalidSnapshot_IsRejectedWhenStagesDoNotMatch()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var requested = CreateStart(
            WorkflowYamlSerializer.ToJson(new WorkflowDefinition(new[]
            {
                new StageDefinition("other", Array.Empty<TaskDefinition>(), Array.Empty<CheckDefinition>()),
            })));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            participant.BindAsync(requested, "start-1", expectedRevision: null));
        Assert.Null(await store.LoadAsync("run-1"));
    }

    [Fact]
    public async Task BindAsync_ReplayWithSameSnapshot_IsAlreadyApplied()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var requested = CreateStart(BuildDefinitionJson());

        var first = await participant.BindAsync(requested, "start-1", expectedRevision: null);
        var replay = await participant.BindAsync(requested, "start-1", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, first.Outcome);
        Assert.Equal(WorkflowRunBindingOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task BindAsync_ConflictingSnapshot_ReturnsConflict()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var original = CreateStart(BuildDefinitionJson());
        var edited = CreateStart(BuildDefinitionJson("other"), "dotnet test");

        var first = await participant.BindAsync(original, "start-1", expectedRevision: null);
        var conflict = await participant.BindAsync(edited, "start-2", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, first.Outcome);
        Assert.Equal(WorkflowRunBindingOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task BindAsync_ConflictingVerificationCommand_ReturnsConflict()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var original = CreateStart(BuildDefinitionJson());
        var edited = CreateStart(BuildDefinitionJson(), "dotnet test");

        var first = await participant.BindAsync(original, "start-1", expectedRevision: null);
        var conflict = await participant.BindAsync(edited, "start-2", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, first.Outcome);
        Assert.Equal(WorkflowRunBindingOutcome.Conflict, conflict.Outcome);
    }

    private static BoundWorkflowStart CreateStart(string? definitionJson, string? verificationCommand = "npm run verify") => new(
        WorkflowRunId: "run-1",
        ProjectId: "project-1",
        IssueNumber: 42,
        EpicNumber: null,
        ExplicitProfileId: "mohist/github-pr",
        ProfileId: "mohist/github-pr",
        Stages: [new BoundStageStructure("build", RequiresApproval: false)],
        Metadata: new WorkflowRunMetadata(
            "Issue 42",
            CreatedAt,
            ProjectId: "project-1",
            IssueNumber: 42),
        Workspace: new WorkspaceIdentity("/worktrees/issue-42", "issue/42"),
        DefinitionJson: definitionJson,
        VerificationCommand: verificationCommand);

    private static string BuildDefinitionJson(string stage = "build") =>
        WorkflowYamlSerializer.ToJson(new WorkflowDefinition(new[]
        {
            new StageDefinition(
                stage,
                new[] { new TaskDefinition("verify", "Verify", "core/script") },
                Array.Empty<CheckDefinition>()),
        }));

    private sealed class FakeWorkflowRunStore : IWorkflowRunStore
    {
        private WorkflowRun? _run;

        public int SaveCount { get; private set; }

        public Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
        {
            _run = run;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task SaveAsync(
            WorkflowRun run,
            IReadOnlyList<WorkflowEvent> events,
            CancellationToken ct = default) => SaveAsync(run, ct);

        public Task SaveWithArtifactsAsync(
            WorkflowRun run,
            IReadOnlyList<WorkflowEvent> events,
            WorkflowArtifactBindingIntent artifacts,
            CancellationToken ct = default) => SaveAsync(run, ct);

        public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default) =>
            Task.FromResult(_run?.Id == workflowRunId ? _run : null);

        public Task DeleteAsync(string workflowRunId, CancellationToken ct = default)
        {
            if (_run?.Id == workflowRunId)
                _run = null;
            return Task.CompletedTask;
        }
    }
}
