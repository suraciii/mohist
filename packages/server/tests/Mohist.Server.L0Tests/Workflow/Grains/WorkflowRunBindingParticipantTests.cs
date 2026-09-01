using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Grains;

public sealed class WorkflowRunBindingParticipantTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Bind_CreatesCompleteBinding_AndRejectsConflictingReplay()
    {
        var store = new FakeWorkflowRunStore();
        var participant = new WorkflowRunBindingParticipant(store);
        var requested = CreateStart("mohist/github-pr");

        var applied = await participant.BindAsync(requested, "start-1", expectedRevision: null);
        var replayed = await participant.BindAsync(requested, "start-1", expectedRevision: null);
        var conflicted = await participant.BindAsync(CreateStart("mohist/local"), "start-2", expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.Applied, applied.Outcome);
        Assert.Equal(WorkflowRunBindingOutcome.AlreadyApplied, replayed.Outcome);
        Assert.Equal(WorkflowRunBindingOutcome.Conflict, conflicted.Outcome);
        Assert.Equal(1, store.SaveCount);

        var stored = Assert.IsType<WorkflowRun>(await store.LoadAsync(requested.WorkflowRunId));
        Assert.Equal(WorkflowRunStatus.Created, stored.Status);
        Assert.Equal("mohist/github-pr", stored.WorkflowProfileId);
        Assert.Equal("build", Assert.Single(stored.Stages).Id);
        Assert.Equal("issue/42", stored.Workspace?.Branch);
        Assert.Equal("project-1", stored.Metadata.ProjectId);
        Assert.Equal(42, stored.Metadata.IssueNumber);
    }

    private static BoundWorkflowStart CreateStart(string profileId) => new(
        WorkflowRunId: "run-1",
        ProjectId: "project-1",
        IssueNumber: 42,
        EpicNumber: null,
        ExplicitProfileId: profileId,
        ProfileId: profileId,
        Stages: [new BoundStageStructure("build", RequiresApproval: false)],
        Metadata: new WorkflowRunMetadata(
            "Issue 42",
            CreatedAt,
            ProjectId: "project-1",
            IssueNumber: 42),
        Workspace: new WorkspaceIdentity("/worktrees/issue-42", "issue/42"),
        DefinitionJson: WorkflowYamlSerializer.ToJson(new WorkflowDefinition(new[]
        {
            new StageDefinition("build", new[] { new TaskDefinition("verify", "Verify", "core/script") }, Array.Empty<CheckDefinition>()),
        })),
        VerificationCommand: "npm run verify");

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
