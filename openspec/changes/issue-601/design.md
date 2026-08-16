## Context

Issue 601 addresses a P1 failure in Workflow task completion. The current runner executes an Action, runs `enforceCleanWorktree`, and reports a failed result when bounded same-session cleanup cannot make the worktree clean. This can discard the distinction between a valid Action result and uncertain workspace cleanup. The runner already has a durable `WorkResultJournal`, but it stores only the projected `WorkItemResult`; it does not store authoritative Action or Git evidence. The server currently accepts a plain `WorkResult` in `WorkflowReportService` and applies task, artifact, and status changes through `WorkflowGrain.ReceiveTaskReportAsync`.

The proposal and `workflow-task-commit-boundary` specification require a durable boundary before reporting, explicit `committed-clean`, `dirty`, and `unconfirmed` outcomes, exact execution identity, replay/conflict handling, and cleanup that cannot destroy task output. The implementation spans the TypeScript runner execution/reporting path, the C# runner-to-Workflow contract and Workflow aggregate, workspace identity/registry code, status projections, and deterministic tests. No external dependency is needed.

## Goals / Non-Goals

**Goals:**

- Persist one immutable `ActionCompletion` and matching `CommitReceipt` for each Workflow task attempt before the result is reported.
- Carry and validate the complete execution identity: Workflow run, stage, task attempt, work, owner, runner, workspace, and workspace generation.
- Classify workspace evidence as exactly `committed-clean`, `dirty`, or `unconfirmed`, while preserving the Action's own success or failure semantics.
- Make the server's accepted boundary durable before applying task, stage, run, artifact, variable, or status projections.
- Replace cleanup-as-settlement with a bounded, idempotent, fenced recovery operation limited to explicitly scoped generated paths.
- Make local persistence, report delivery, cleanup operations, and Workflow settlement idempotent and conflict-safe.
- Apply the same boundary to generic Workflow Actions and Pi/OpenCode-backed Workflow tasks, with deterministic filesystem and clock tests.

**Non-Goals:**

- Rerunning an Action because cleanup or workspace verification is uncertain.
- Automatically reverting, resetting, checking out, cleaning, deleting, or replacing a workspace to force a clean Git status.
- Changing the semantics of checks reports, standalone AgentJob ownership, or unrelated workspace retention policy except where they consume the new status contract.
- Making dirty or unconfirmed work a business failure merely to preserve the existing terminal state machine.
- Introducing a new external persistence or locking service.

## Decisions

### 1. Use an immutable completion boundary with mutable recovery state

Add a typed `TaskCompletionBoundary` containing `ActionCompletion`, `CommitReceipt`, execution identity, the initial arbitrated outcome, and a stable fingerprint. Store it on the persisted Workflow task attempt using the existing `WorkflowRunStore` transaction. Keep cleanup leases, cleanup evidence, later verification observations, and projection progress in a separate mutable `WorkspaceRecovery` value; cleanup never edits the boundary.

The runner's versioned `WorkResultJournal` will atomically store the same boundary together with the report payload using its existing temporary-file-and-rename mechanism. A local write failure leaves the work fenced and non-reportable. On the server, acceptance is a first durable commit of the boundary. Projection application then consumes that stored boundary, so activation can resume an accepted-but-not-yet-applied result without rerunning the Action.

Embedding the boundary in the existing Workflow aggregate is preferred over a new standalone receipt table because the aggregate already provides per-run concurrency and atomic state-plus-event persistence. A separate table would improve querying, but would require a cross-store transaction or an additional inbox protocol and would create a second source of truth for task identity. A separate normalized read model can be added later if receipt search becomes a product requirement.

### 2. Probe and arbitrate before cleanup, then report the evidence

The runner will capture the Action result independently of cleanup, probe the expected workspace generation, and construct the immutable boundary before starting any cleanup. The receipt records expected and observed workspace identity, generation, branch, HEAD, tree, staged paths, unstaged paths, untracked paths, probe authority, and a verification reason when authority is unavailable. The workspace marker, dispatch envelope, and runner workspace registry will gain a stable workspace identity and generation; allocating a fresh workspace always changes both the identity and generation.

The arbitration module will require an authoritative identity/generation and branch match, successful HEAD/tree/status probes, and an empty status for `committed-clean`. An authoritative matching probe with any staged, unstaged, or untracked paths is `dirty`. Missing or mismatched identity, generation, branch, HEAD, tree, status, or probe completion is `unconfirmed`. A conclusive Action failure remains a failed Action even if the workspace outcome is dirty or unconfirmed.

This replaces the current `enforceCleanWorktree` success gate. It preserves the existing Action normalization and artifact capture, but moves cleanup after the durable completion boundary. The alternative of retaining the cleanup loop and adding a second receipt afterward would still allow cleanup timing to decide business success and would make the receipt describe a mutated workspace rather than the Action's completion boundary.

### 3. Make cleanup a server-authorized, scoped operation

Introduce cleanup lease operations keyed by the exact execution identity and workspace generation. The Workflow boundary grants at most one current fence with an expiration and work budget. Cleanup requests include an idempotency operation id, fence, generation, and an immutable path scope. Replaying an operation returns its existing result. A stale, expired, or superseded fence is rejected before filesystem mutation.

The runner cleanup implementation will validate the workspace marker, managed-root containment, generation, and path containment before each mutation. It may remove only generated temporary paths explicitly recorded in the boundary's cleanup scope. It will not invoke broad `git reset`, `git clean`, checkout, restore, branch replacement, or whole-workspace deletion. Cleanup probes and mutations are recorded as separate evidence. If an unscoped task change remains, the result stays dirty; recovery may inspect it, obtain a new authoritative verification observation, or allocate a fresh generation.

A runner-local lock alone was rejected because it cannot stop an old runner from acting after the server rebinds a task to a new generation. A broad cleanup prompt was rejected because it can destroy task commits, declared outputs, or unrelated user changes while appearing to improve status.

### 4. Persist first, then project normal or recoverable settlement

Extend the runner report contract with the immutable boundary, arbitrated outcome, cleanup evidence, and exact identity. `WorkflowReportService` will validate and accept the boundary before artifact binding or Workflow projection. Exact replays return the original durable decision. A different completion, receipt, outcome, generation, branch, HEAD, tree, or status for the same identity is a conflict and cannot alter the stored record.

After the boundary commit:

- `committed-clean` is translated into the existing successful task path. Artifact binding remains idempotent and artifact events precede task completion events.
- `dirty` and `unconfirmed` store Action output, artifact references, receipt evidence, cleanup evidence, workspace identity, and a recovery deadline/action in a dedicated recoverable settlement value analogous to the existing Agent result settlement. The task remains nonterminal and the task, stage, and run projections do not become business failures.
- A later authorized clean verification consumes the preserved completion, binds/publishes artifacts once, and completes the task without creating another attempt or rerunning the Action.
- A conclusive Action failure uses the existing failed-task semantics after its boundary is durable.

The existing Agent result settlement model is a useful pattern for nonterminal attention, but it will not be overloaded with Git-specific fields. Status mappers and Workflow views will expose explicit dirty/unconfirmed state, reason, deadline, workspace identity/generation, and next recovery action. Runtime session idle/stop/transcript facts remain observations only.

### 5. Use one runtime-neutral pipeline and deterministic seams

Generic Actions, inline `mohist/pi`, and inline `mohist/opencode` will all pass through one completion-boundary builder in the runner after Action result normalization and before report promotion. Runtime adapters continue to own execution and session reporting; none may settle the Workflow directly from a transcript, stop response, or in-memory result.

Git probing, filesystem mutation, lease time, recovery deadlines, and report transport will be injected behind the existing runner resource/test-double seams. Server tests will exercise the aggregate and report service with a controllable `TimeProvider`. This keeps clean, dirty, unconfirmed, timeout, stale-fence, replay, and conflict behavior reproducible without relying on wall-clock timing or a real repository.

## Risks / Trade-offs

- [A process can fail after the server stores the boundary but before projections are applied] -> Keep projection progress and the original report data in durable Workflow state; reconcile pending boundaries on activation and make artifact binding and settlement idempotent.
- [Dirty task changes may remain on disk for a long time] -> Expose a recoverable outcome with explicit workspace identity, generation, scope, deadline, and next action; preserve the workspace or allocate a fresh generation instead of silently deleting changes.
- [A probe can be incomplete or disagree with the current workspace] -> Set `authoritative=false`, classify as `unconfirmed`, record the exact reason, and prohibit both successful clean settlement and business failure from that observation.
- [Old runners cannot provide a completion boundary] -> Use a negotiated compatibility mode during rollout; legacy reports must not be treated as authoritative once enforcement is enabled.
- [Receipt output and path evidence increase persisted Workflow state] -> Store artifact references rather than binary content, bound diagnostic text, and retain only the immutable boundary plus bounded cleanup/verification evidence.
- [Concurrent cleanup workers can race a workspace rebind] -> Use a server-owned lease/fence, generation checks, operation idempotency, and a final local containment/marker check before each mutation.
- [A malformed or conflicting retry could create duplicate projections] -> Compare a canonical boundary fingerprint and exact identity before accepting; return the original decision for replays and reject conflicts without appending events.

## Migration Plan

1. Add additive domain fields, durable boundary/recovery serialization, report DTO fields, status views, and server lease/verification operations. Existing runs deserialize with no boundary and retain their current state.
2. Deploy the server in compatibility mode. It understands the new report but continues to recognize legacy runner capability declarations. New fields are ignored only for legacy identities; they are not used to manufacture an authoritative clean result.
3. Upgrade runners. The runner writes the boundary to the local journal before reporting, sends the enriched report, and stops treating same-session cleanup as the success/failure decision. Existing durable started fences are reconciled as unconfirmed unless a matching authoritative runtime result exists.
4. Enable enforcement after all active runners advertise boundary support. From this point, a missing boundary is rejected or retained as recoverable unconfirmed, never settled as success or failure solely from session activity.
5. Monitor replay/conflict rates, pending recoveries, lease expiry, and dirty/unconfirmed age. Reclaim workspaces only through the explicit generation-aware recovery/reclamation path.

Rollback requires disabling enforcement and draining upgraded runners before reverting the server contract. The additive boundary fields and persisted state can remain in place. A direct rollback to a server that ignores enriched reports while upgraded runners are active is unsafe because it would lose the new settlement semantics; the compatibility server must remain available for that transition.

## Open Questions

- What user-facing labels and recovery actions should the Web and CLI expose for `dirty` versus `unconfirmed` outcomes?
- What default lease duration, cleanup work budget, and recovery deadline should be configured, and should projects be allowed to override them?
- Should a clean boundary permit immediate normal Workflow advancement while separately scoped generated-artifact cleanup continues, or should cleanup evidence be required before advancement even though it cannot change the boundary?
- What retention period and operator access path are required for immutable completion/receipt evidence after a Workflow run becomes terminal?
