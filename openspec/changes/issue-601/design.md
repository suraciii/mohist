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

Add a typed `TaskCompletionBoundary` containing `ActionCompletion`, `CommitReceipt`, execution identity, the initial arbitrated outcome, and a stable fingerprint. Store it on the persisted Workflow task attempt using the existing `WorkflowRunStore` transaction. Keep cleanup leases, cleanup evidence, later verification observations, authorized source-adoption records, and projection progress in a separate mutable `WorkspaceRecovery` value; cleanup never edits the boundary.

`WorkspaceRecovery` owns a `WorkflowTaskRecoveryState` of `dirty` or `unconfirmed`, its reason, deadline/next action, cleanup scope, lease/fence history, and bounded evidence. A later observation is a typed `WorkspaceVerification`, never a replacement `CommitReceipt`. It contains its own idempotency key, exact execution identity, the immutable boundary fingerprint it evaluates, workspace identity/generation, current branch/HEAD/tree/status evidence, authority and reason, and an optional authorized source-adoption operation id. A verification may report a different current HEAD or tree only when it references the accepted source-adoption operation; it never changes the first receipt.

The runner's versioned `WorkResultJournal` will atomically store the same boundary together with the report payload using its existing temporary-file-and-rename mechanism. A local write failure leaves the work fenced and non-reportable. On the server, acceptance is a first durable commit of the boundary. Projection application then consumes that stored boundary, so activation can resume an accepted-but-not-yet-applied result without rerunning the Action. Replaying one `WorkspaceVerification` idempotency key compares its canonical payload and returns the stored observation; a different payload under that key is rejected.

Embedding the boundary in the existing Workflow aggregate is preferred over a new standalone receipt table because the aggregate already provides per-run concurrency and atomic state-plus-event persistence. A separate table would improve querying, but would require a cross-store transaction or an additional inbox protocol and would create a second source of truth for task identity. A separate normalized read model can be added later if receipt search becomes a product requirement.

### 2. Probe and arbitrate before cleanup, then report the evidence

The runner will capture the Action result independently of cleanup, probe the expected workspace generation, and construct the immutable boundary before starting any cleanup. The receipt records expected and observed workspace identity, generation, branch, HEAD, tree, staged paths, unstaged paths, untracked paths, probe authority, and a verification reason when authority is unavailable. The workspace marker, dispatch envelope, and runner workspace registry will gain a stable workspace identity and generation; allocating a fresh workspace always changes both the identity and generation.

The arbitration module will require an authoritative identity/generation and branch match, successful HEAD/tree/status probes, and an empty status for `committed-clean`. An authoritative matching probe with any staged, unstaged, or untracked paths is `dirty`. Missing or mismatched identity, generation, branch, HEAD, tree, status, or probe completion is `unconfirmed`. A conclusive Action failure remains a failed Action even if the workspace outcome is dirty or unconfirmed.

This replaces the current `enforceCleanWorktree` success gate. It preserves the existing Action normalization and artifact capture, but moves cleanup after the durable completion boundary. The alternative of retaining the cleanup loop and adding a second receipt afterward would still allow cleanup timing to decide business success and would make the receipt describe a mutated workspace rather than the Action's completion boundary.

### 3. Make cleanup a server-authorized, scoped operation

Introduce cleanup lease operations keyed by the exact execution identity and workspace generation. The Workflow boundary grants at most one current fence with an expiration and work budget. Cleanup requests include an idempotency operation id, fence, generation, and an immutable path scope. Replaying an operation returns its existing result. A stale, expired, or superseded fence is rejected before filesystem mutation.

The runner cleanup implementation will validate the workspace marker, managed-root containment, generation, and path containment before each mutation. It may remove only generated temporary paths explicitly recorded in the boundary's cleanup scope. It will not invoke broad `git reset`, `git clean`, checkout, restore, branch replacement, or whole-workspace deletion. Cleanup probes and mutations are recorded as separate evidence. If an unscoped task change remains, the result stays dirty; recovery may inspect it, obtain a `WorkspaceVerification`, or allocate a fresh generation.

Legal task-source changes have one explicit recovery path: `AdoptTaskSourceChanges`. A recovery operator with the Workflow permission and the current server-issued fence for the exact boundary submits an idempotency key and an explicit repository-relative source-path allowlist. The server records that allowlist and requires it to be disjoint from generated cleanup paths, declared outputs, and recorded artifacts. The runner revalidates the marker, identity, generation, containment, and current diff, then may create one task commit containing only the allowlisted source paths. It uses path-limited `git add`/commit operations; it never deletes or resets files to satisfy cleanup. A rejected, stale, or failed adoption request performs no mutation and preserves every source/output/artifact file. A successful adoption is recorded as recovery evidence and must be followed by a `WorkspaceVerification` that names the adoption operation; it does not rewrite the original receipt.

A runner-local lock alone was rejected because it cannot stop an old runner from acting after the server rebinds a task to a new generation. A broad cleanup prompt was rejected because it can destroy task commits, declared outputs, or unrelated user changes while appearing to improve status.

### 4. Persist first, then project normal or recoverable settlement

Extend the runner report contract with the immutable boundary, arbitrated outcome, cleanup evidence, and exact identity. `WorkflowReportService` will validate and accept the boundary before artifact binding or Workflow projection. Exact replays return the original durable decision. A different completion, receipt, outcome, generation, branch, HEAD, tree, or status for the same identity is a conflict and cannot alter the stored record.

After the boundary commit, the persisted state and allowed actions are explicit:

- `committed-clean`: no recoverable hold is created. The task transitions from `Running` to `Completed`, the existing stage/run advancement path runs, and artifact binding/events remain idempotent and ordered before task completion.
- `dirty`: `TaskRun.Status` remains `Running` and carries `WorkflowTaskRecoveryState=dirty`; `StageRun.Status` and `WorkflowRun.Status` remain `Running`, with the original runner/workspace assignment and boundary identity retained. The wire task, containing stage, and run status are `recoverable-dirty`, with the reason, deadline/next action, workspace identity/generation, cleanup scope, and preserved output/artifacts exposed. `PendingWork` is null, `NextWork` returns null, claims and normal reports are rejected, and stage/run advancement, retries, and reminders cannot turn the state into a business failure. The allowed operations are a current-fence scoped cleanup, inspection, `WorkspaceVerification`, `AdoptTaskSourceChanges`, fresh-generation allocation, or explicit stop.
- `unconfirmed`: the same persisted lifecycle values and dispatch fence are used, but `WorkflowTaskRecoveryState=unconfirmed` and the wire values are `recoverable-unconfirmed`; the verification reason is required. No cleanup mutation or successful settlement is allowed until a current authoritative verification exists. The same recovery operations are allowed, with an additional requirement that a verification must repair the missing/mismatched authority before settlement.
- A `WorkspaceVerification` with authoritative identity/generation, expected branch, empty status, and either unchanged HEAD/tree or a referenced accepted source-adoption commit marks the recovery settled and invokes the same idempotent `CompleteTask` path. It binds/publishes the boundary's artifacts once, never creates an attempt, and never replaces the initial receipt. A dirty or unconfirmed verification only appends evidence and leaves the corresponding recovery state in place.
- An authoritative Action failure, including a failure before an Action result exists, remains the existing `TaskRun=Failed`, `StageRun=Failed`, and `WorkflowRun=Failed` path after its boundary is durable. Its workspace outcome is retained as evidence but cannot turn that conclusive failure into success. A persistence or report-delivery failure is different: it leaves the local started fence/boundary pending and produces no task, stage, or run transition.
- An explicit stop/cancel command is the only ordinary command allowed to end a recoverable attempt: it marks the task `Cancelled`, retains `WorkspaceRecovery` and the immutable boundary for protected reclamation, and marks the run `Stopped` without emitting `TaskFailed`, `StageFailed`, or `WorkflowRunFailed`. The existing stage enum remains `Running` in persisted state; status projection derives `stopped` for a stage under a stopped run. No late report or verification can reopen the cancelled attempt.

The existing Agent result settlement model is a useful pattern for nonterminal attention, but it will not be overloaded with Git-specific fields. Status mappers and Workflow views will expose the explicit dirty/unconfirmed values and recovery actions above. Runtime session idle/stop/transcript facts remain observations only, and do not clear the recovery hold.

### 5. Use one runtime-neutral pipeline and deterministic seams

Generic Actions, inline `mohist/pi`, and inline `mohist/opencode` will all pass through one completion-boundary builder at the outer Workflow-task execution adapter, before any result can be promoted to a report. The builder is entered at task admission and wraps every terminal return: workspace setup failure, unknown/removed Action, unresolved or invalid input, start branch-probe failure, Action throw or normalized Action failure, end branch-probe failure, artifact capture failure, output/set-variable failure, and the outer unexpected-error catch. A pre-Action failure gets an `ActionCompletion` with `actionStarted=false`, a phase-specific error, and empty output/artifact references; its `CommitReceipt` carries the expected execution identity plus `authoritative=false` and the precise unavailable-probe reason. A known Action result is retained when a later capture/projection step fails. Only after this boundary is durably journaled may cleanup, report delivery, or server settlement begin. Report serialization/transport failure retains the journal record and never creates a business failure. Runtime adapters continue to own execution and session reporting; none may settle the Workflow directly from a transcript, stop response, or in-memory result.

The boundary wrapper applies to `ownerKind=workflow`, `workType=task`; checks and AgentJob use their existing separate report contracts. Git probing, filesystem mutation, lease time, recovery deadlines, and report transport will be injected behind the existing runner resource/test-double seams. Server tests will exercise the aggregate and report service with a controllable `TimeProvider`. This keeps clean, dirty, unconfirmed, pre-Action failure, timeout, stale-fence, replay, and conflict behavior reproducible without relying on wall-clock timing or a real repository.

## Risks / Trade-offs

- [A process can fail after the server stores the boundary but before projections are applied] -> Keep projection progress and the original report data in durable Workflow state; reconcile pending boundaries on activation and make artifact binding and settlement idempotent.
- [Dirty task changes may remain on disk for a long time] -> Expose a recoverable outcome with explicit workspace identity, generation, scope, deadline, and next action; preserve the workspace or allocate a fresh generation instead of silently deleting changes.
- [A probe can be incomplete or disagree with the current workspace] -> Set `authoritative=false`, classify as `unconfirmed`, record the exact reason, and prohibit both successful clean settlement and business failure from that observation.
- [A pre-v1 runner cannot provide a completion boundary] -> Fail closed at the report boundary. Do not pass its plain result to the legacy settlement path; record only a recoverable missing-boundary observation when the active execution identity is still known, otherwise reject and retain the journal fence for operator reconciliation.
- [Receipt output and path evidence increase persisted Workflow state] -> Store artifact references rather than binary content, bound diagnostic text, and retain only the immutable boundary plus bounded cleanup/verification evidence.
- [Concurrent cleanup workers can race a workspace rebind] -> Use a server-owned lease/fence, generation checks, operation idempotency, and a final local containment/marker check before each mutation.
- [A malformed or conflicting retry could create duplicate projections] -> Compare a canonical boundary fingerprint and exact identity before accepting; return the original decision for replays and reject conflicts without appending events.

## Migration Plan

1. Ship the v1 boundary schema, report DTO, recovery state, `WorkspaceVerification`, source-adoption operation, status views, and lease operations as one fail-closed contract. The server report admission path must validate the boundary before translation, artifact binding, or the existing `WorkResult` settlement path; a plain legacy result is never eligible for normal success/failure settlement.
2. Quiesce Workflow dispatch and drain or stop every pre-v1 runner before enabling the v1 report endpoint. Runner capability negotiation is an admission prerequisite, not a compatibility mode: a runner that cannot emit the boundary is refused new Workflow task claims and cannot settle an existing task from a legacy payload.
3. Upgrade runners and re-enable Workflow dispatch only after the runner writes the boundary before report promotion. Generic, Pi, and OpenCode tasks use the same wrapper. A v1 runner that finds a pre-existing started journal fence never reruns the Action: it emits a missing-boundary `unconfirmed` recovery observation if the exact task/work/runner/workspace identity can be validated, or retains the fence and reports an operator-reconciliation error if it cannot. A pre-v1 completed journal entry is never replayed as a plain result; it follows the same missing-boundary path.
4. Reconcile already-started legacy executions under the one contract. The server either stores a non-settling `WorkflowTaskRecoveryState=unconfirmed` with reason `boundary-missing` and a deadline/next action, preserving the task assignment and workspace, or rejects the observation without mutation when identity validation fails. Neither branch emits success, `TaskFailed`, `StageFailed`, or `WorkflowRunFailed`; no session activity can upgrade it. An operator must inspect/adopt source changes or explicitly stop/cancel it.
5. Monitor rejected legacy reports, missing-boundary recoveries, replay/conflict rates, pending verifications, lease expiry, and dirty/unconfirmed age. Reclaim workspaces only through the explicit generation-aware recovery/reclamation path. There is no rollback to a server that understands only legacy settlement: an application rollback is permitted only after Workflow dispatch is quiesced and all affected fences are reconciled under v1, otherwise the fail-closed v1 server remains in place.

## Open Questions

- What default lease duration, cleanup work budget, and recovery deadline should be configured, and should projects be allowed to override them?
- Should a clean boundary permit immediate normal Workflow advancement while separately scoped generated-artifact cleanup continues, or should cleanup evidence be required before advancement even though it cannot change the boundary?
- What retention period and operator access path are required for immutable completion/receipt evidence after a Workflow run becomes terminal?
