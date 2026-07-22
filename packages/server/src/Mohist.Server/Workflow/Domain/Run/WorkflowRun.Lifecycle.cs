using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Domain.Run;

public static partial class WorkflowRunExtensions
{
    extension(WorkflowRun)
    {
        public static WorkflowRun Create(
            string id,
            WorkflowDefinition definition,
            DateTimeOffset now,
            WorkflowRunMetadata? metadata = null)
        {
            if (definition.Stages.Count == 0)
                throw new InvalidOperationException("WorkflowDefinition requires at least one stage");

            var stages = definition.Stages
                .Select((def, i) => new StageRun
                {
                    Id = def.Stage,
                    Attempt = 1,
                    RequiresApproval = def.RequiresApproval,
                    Status = StageRunStatus.Pending
                })
                .ToList();

            return new WorkflowRun
            {
                Id = id,
                Metadata = metadata ?? new WorkflowRunMetadata(null, now),
                Status = WorkflowRunStatus.Created,
                CurrentStageId = stages[0].Id,
                Stages = stages,
            };
        }

        public static WorkflowRun Create(
            string id,
            WorkflowStructure structure,
            DateTimeOffset now,
            WorkflowRunMetadata? metadata = null)
        {
            if (structure.Stages.Count == 0)
                throw new InvalidOperationException("WorkflowStructure requires at least one stage");

            var stages = structure.Stages
                .Select(s => new StageRun
                {
                    Id = s.Stage,
                    Attempt = 1,
                    RequiresApproval = s.RequiresApproval,
                    Status = StageRunStatus.Pending
                })
                .ToList();

            return new WorkflowRun
            {
                Id = id,
                Metadata = metadata ?? new WorkflowRunMetadata(null, now),
                Status = WorkflowRunStatus.Created,
                CurrentStageId = stages[0].Id,
                Stages = stages,
            };
        }
    }

    extension(WorkflowRun run)
    {
        public StageRun CurrentStage()
        {
            if (run.CurrentStageId is null)
                throw new InvalidOperationException("WorkflowRun has no current stage");
            return run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId)
                ?? throw new InvalidOperationException($"Current stage {run.CurrentStageId} not found");
        }

        public IReadOnlyList<WorkflowEvent> Start(DateTimeOffset now)
        {
            if (run.Status != WorkflowRunStatus.Created && run.Status != WorkflowRunStatus.Paused)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}");

            var wasPaused = run.Status == WorkflowRunStatus.Paused;
            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            SetStatusAndTrackReadySince(run, wasPaused
                ? ActiveOrWaitingForDispatchStatus(run)
                : WorkflowRunStatus.Pending,
                now);
            run.StartedAt ??= now;
            return wasPaused
                ? [new WorkflowRunResumed()]
                : [new WorkflowRunStarted(), new StageStarted(current.Id)];
        }

        public IReadOnlyList<WorkflowEvent> Pause()
        {
            if (run.Status is not (WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running))
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, pause requires an executing state");
            run.Status = WorkflowRunStatus.Paused;
            return [new WorkflowRunPaused()];
        }

        public IReadOnlyList<WorkflowEvent> Resume(DateTimeOffset now)
        {
            if (run.Status != WorkflowRunStatus.Paused)
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, resume requires Paused");

            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            ApplyActiveOrWaitingForDispatchStatus(run, now);
            return [new WorkflowRunResumed()];
        }

        public IReadOnlyList<WorkflowEvent> Stop()
        {
            if (run.Status is not (WorkflowRunStatus.Created or WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running or WorkflowRunStatus.AwaitingApproval or WorkflowRunStatus.Paused or WorkflowRunStatus.Failed))
                throw new InvalidOperationException($"WorkflowRun is {run.Status}, stop requires a non-terminal started state");

            run.ClearStaleApprovalGate();
            run.Status = WorkflowRunStatus.Stopped;
            return [new WorkflowRunStopped()];
        }

        /// <summary>
        /// issue-417 T-006 (D4): input-idempotent start called by the
        /// durable <c>IssueWorkStarted</c> handler. Idempotency rules:
        /// <list type="bullet">
        ///   <item>A run with no <see cref="WorkflowRun.Repository"/>
        ///     context yet assigns the supplied context and emits
        ///     <c>WorkflowRunStarted</c> + <c>StageStarted</c> exactly
        ///     once.</item>
        ///   <item>A run already started with the same repository
        ///     context (matches <see cref="WorkflowRepositoryContext.Name"/>,
        ///     <see cref="WorkflowRepositoryContext.GitUrl"/>,
        ///     and <see cref="WorkflowRepositoryContext.BaseBranch"/>)
        ///     succeeds as a duplicate-replay no-op (no events).</item>
        ///   <item>A different context on the same run id, OR a
        ///     different workspace path/branch, throws
        ///     <see cref="InvalidOperationException"/> — the run is
        ///     corrupted by the caller and must not auto-correct.</item>
        ///   <item>Generic (non-Issue-backed) starts may pass
        ///     <c>repository: null</c>; the assignment is then omitted
        ///     entirely.</item>
        ///   <item>The supplied <see cref="WorkspaceIdentity"/>
        ///     also participates in the input check; a different
        ///     workspace path from the one already persisted indicates
        ///     corruption too.</item>
        /// </list>
        /// </summary>
        public IReadOnlyList<WorkflowEvent> EnsureStarted(
            WorkflowRepositoryContext? repository,
            WorkspaceIdentity? workspace,
            DateTimeOffset now,
            WorkflowRunMetadata? metadata = null)
        {
            if (run.Status != WorkflowRunStatus.Created)
            {
                // Replay path: refuse to mutate state but be quiet
                // when input matches what we already recorded.
                if (repository is null && workspace is null)
                {
                    if (run.Repository is null && run.Workspace is null)
                        return [];
                    throw new InvalidOperationException(
                        $"WorkflowRun '{run.Id}' already started but replay passed null context");
                }
                if (run.Repository is null && run.Workspace is null
                    && repository is null && workspace is null)
                    return [];

                if (!WorkflowRepositoryContextEquals(run.Repository, repository))
                    throw new InvalidOperationException(
                        $"WorkflowRun '{run.Id}' already started with conflicting repository context");
                if (!WorkspaceIdentityEquals(run.Workspace, workspace))
                    throw new InvalidOperationException(
                        $"WorkflowRun '{run.Id}' already started with conflicting workspace identity");
                if (!WorkflowMetadataIdentityEquals(run.Metadata, metadata))
                    throw new InvalidOperationException(
                        $"WorkflowRun '{run.Id}' already started with conflicting Issue context");

                return [];
            }

            // Fresh start: persist the snapshot and emit events once.
            run.AssignRepositoryContext(repository);
            run.Workspace = workspace;

            var current = run.CurrentStage();
            if (current.Status == StageRunStatus.Pending)
                current.Status = StageRunStatus.Running;

            SetStatusAndTrackReadySince(run, WorkflowRunStatus.Pending, now);
            run.StartedAt = now;
            return [new WorkflowRunStarted(), new StageStarted(current.Id)];
        }

        private static bool WorkflowRepositoryContextEquals(
            WorkflowRepositoryContext? a,
            WorkflowRepositoryContext? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            return string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                && string.Equals(a.GitUrl, b.GitUrl, StringComparison.Ordinal)
                && string.Equals(a.BaseBranch, b.BaseBranch, StringComparison.Ordinal);
        }

        private static bool WorkspaceIdentityEquals(WorkspaceIdentity? a, WorkspaceIdentity? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            return string.Equals(a.Path, b.Path, StringComparison.Ordinal)
                && string.Equals(a.Branch ?? string.Empty, b.Branch ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(a.ChangeDir ?? string.Empty, b.ChangeDir ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool WorkflowMetadataIdentityEquals(WorkflowRunMetadata? a, WorkflowRunMetadata? b)
        {
            if (a is null || b is null) return true;
            foreach (var key in new[] { "projectId", "issueId", "issueNumber" })
            {
                var aValue = a.Annotations?.GetValueOrDefault(key);
                var bValue = b.Annotations?.GetValueOrDefault(key);
                if (!string.Equals(aValue, bValue, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
