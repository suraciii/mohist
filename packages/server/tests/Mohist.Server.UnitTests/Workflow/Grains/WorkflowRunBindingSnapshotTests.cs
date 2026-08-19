using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grains;

/// <summary>
/// Verifies the <c>BoundWorkflowDefinitionJson</c> snapshot: capture at
/// <c>BindAsync</c> time, durable persistence on the run, inclusion in
/// idempotency replay/conflict checks, and the snapshot is independent of
/// later profile edits (so mixed-version rollouts cannot change a run's lane
/// mode or task definitions).
/// </summary>
public sealed class WorkflowRunBindingSnapshotTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BindAsync_PersistsDefinitionJson_OnRun()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var requested = CreateStart("mohist/pi", definitionJson: "{\"stages\":[]}");

        var applied = await participant.BindAsync(requested, "start-1", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, applied.Outcome);
        var stored = Assert.IsType<WorkflowRun>(await store.LoadAsync(requested.WorkflowRunId));
        Assert.Equal("{\"stages\":[]}", stored.BoundWorkflowDefinitionJson);
    }

    [Fact]
    public async Task BindAsync_NullDefinitionJson_IsAllowedForLegacyRun()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var requested = CreateStart("mohist/pi", definitionJson: null);

        var applied = await participant.BindAsync(requested, "start-1", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, applied.Outcome);
        var stored = Assert.IsType<WorkflowRun>(await store.LoadAsync(requested.WorkflowRunId));
        Assert.Null(stored.BoundWorkflowDefinitionJson);
    }

    [Fact]
    public async Task BindAsync_ReplayWithSameSnapshot_IsAlreadyApplied()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var requested = CreateStart("mohist/pi", definitionJson: "{\"stages\":[]}");

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
        var original = CreateStart("mohist/pi", definitionJson: "{\"stages\":[]}");
        var edited = CreateStart("mohist/pi", definitionJson: "{\"stages\":[\"build\"]}");

        var first = await participant.BindAsync(original, "start-1", expectedRevision: null);
        var conflict = await participant.BindAsync(edited, "start-2", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, first.Outcome);
        Assert.Equal(WorkflowRunBindingOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task BindAsync_ProfileEditedAfterBinding_DoesNotChangeRunSnapshot()
    {
        // Mixed-version rollout. The run is bound while the profile still
        // contains the aggregate verify task; the snapshot captures that
        // shape. A subsequent profile edit cannot retroactively change the
        // run's bound definition, so the run materializes the aggregate
        // task even after the profile moves to six lanes.
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var aggregateJson = BuildAggregateDefinitionJson();
        var requested = CreateStart("mohist/pi", definitionJson: aggregateJson);

        await participant.BindAsync(requested, "start-1", expectedRevision: null);

        var stored = Assert.IsType<WorkflowRun>(await store.LoadAsync(requested.WorkflowRunId));
        var loaded = WorkflowYamlSerializer.FromJson(stored.BoundWorkflowDefinitionJson!);
        var build = loaded.Stages.Single(s => s.Stage == "build");
        Assert.Single(build.Tasks);
        Assert.Equal("verify", build.Tasks[0].Id);
        Assert.False(VerificationLaneGate.IsLaneEnabledRun(stored));
    }

    [Fact]
    public async Task BindAsync_SixLaneSnapshot_RunIsLaneEnabled()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var sixLaneJson = BuildSixLaneDefinitionJson();
        var requested = CreateStart("mohist/pi", definitionJson: sixLaneJson);

        await participant.BindAsync(requested, "start-1", expectedRevision: null);

        var stored = Assert.IsType<WorkflowRun>(await store.LoadAsync(requested.WorkflowRunId));
        Assert.True(VerificationLaneGate.IsLaneEnabledRun(stored));
    }

    private static BoundWorkflowStart CreateStart(string agentAction, string? definitionJson) => new(
        WorkflowRunId: "run-1",
        ProjectId: "project-1",
        IssueNumber: 42,
        EpicNumber: null,
        ExplicitProfileId: "mohist/github-pr",
        ProfileId: "mohist/github-pr",
        AgentAction: agentAction,
        Stages: [new BoundStageStructure("build", RequiresApproval: false)],
        Metadata: new WorkflowRunMetadata(
            "Issue 42",
            CreatedAt,
            ProjectId: "project-1",
            IssueNumber: 42),
        Workspace: new WorkspaceIdentity("/worktrees/issue-42", "issue/42"),
        DefinitionJson: definitionJson);

    private static string BuildAggregateDefinitionJson() =>
        WorkflowYamlSerializer.ToJson(new WorkflowDefinition(new[]
        {
            new StageDefinition(
                "build",
                new[]
                {
                    new TaskDefinition("verify", "Verify", "core/script"),
                },
                Array.Empty<CheckDefinition>()),
        }));

    private static string BuildSixLaneDefinitionJson() =>
        WorkflowYamlSerializer.ToJson(new WorkflowDefinition(new[]
        {
            new StageDefinition(
                "build",
                VerificationLaneCatalog.LaneIds
                    .Select(id => new TaskDefinition(id, id, "core/script"))
                    .ToList(),
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