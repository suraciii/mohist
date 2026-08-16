### Requirement: Durable action completion and commit receipt
Every Workflow-owned `workType=task` attempt SHALL produce one immutable `ActionCompletion` and one matching `CommitReceipt` for the exact Workflow run, stage, task attempt, work identity, owner, runner identity, workspace identity, and workspace generation. The completion-boundary builder SHALL run on every terminal execution path, including workspace setup failure, unknown or removed Action, unresolved or invalid input, branch-probe failure, Action throw or normalized Action failure, artifact/output/set-variable failure, and the outer unexpected-error catch. A failure before an Action result exists is represented by `actionStarted=false`, a phase-specific error, and empty output/artifact references; its receipt retains the expected identity with `authoritative=false` and the unavailable-probe reason. Checks and AgentJob reports remain separate contracts. `ActionCompletion` SHALL retain the authoritative Action outcome, output, error, and produced artifact references. `CommitReceipt` SHALL retain the observed branch, HEAD, tree, staged paths, unstaged paths, untracked paths, workspace generation, and whether the probe was authoritative. The Runner SHALL persist the pair before delivering a result report, and the Server SHALL persist the accepted pair before applying a task, stage, or run projection. Cleanup activity MUST NOT modify either immutable record.

#### Scenario: A successful Action produces a clean receipt
- **WHEN** an Action returns a successful result and the Runner authoritatively probes the expected workspace generation
- **THEN** the Runner SHALL persist the ActionCompletion and matching CommitReceipt as one durable completion boundary
- **AND** the receipt SHALL contain the observed branch, HEAD, tree, and empty working-tree status
- **AND** no Workflow settlement SHALL be applied before that durable boundary exists

#### Scenario: Local persistence is unavailable after the Action returns
- **WHEN** the Action returns a result but the Runner cannot durably persist the matching completion and receipt
- **THEN** the Runner MUST NOT report the result as settled
- **AND** the task and workspace SHALL remain recoverable for persistence retry or explicit recovery
- **AND** the persistence failure MUST NOT be converted into a business task failure solely because the durable write is unavailable

#### Scenario: Cleanup runs after the completion boundary
- **WHEN** cleanup changes the workspace or removes an allowed generated artifact after the ActionCompletion and CommitReceipt have been persisted
- **THEN** the original ActionCompletion and CommitReceipt SHALL remain immutable
- **AND** the cleanup lease, mutations, probe, and result SHALL be recorded separately from the Action result

#### Scenario: A task fails before an Action result exists
- **WHEN** workspace setup, Action resolution, input resolution/validation, or the start branch probe fails before Action execution returns
- **THEN** the boundary builder SHALL persist an `ActionCompletion` with `actionStarted=false` and the phase-specific failure
- **AND** the matching `CommitReceipt` SHALL retain the expected execution identity and an authoritative=false reason
- **AND** the failure SHALL reach the existing failed-Action settlement only after the boundary is durable

### Requirement: Explicit workspace outcome arbitration
Before settling a Workflow task, the system SHALL classify the persisted ActionCompletion and matching CommitReceipt as exactly one of `committed-clean`, `dirty`, or `unconfirmed`. `committed-clean` SHALL require an authoritative observation of the expected workspace generation, expected branch, HEAD, tree, and empty working-tree status. `dirty` SHALL require an authoritative observation of the expected workspace identity and generation with staged, unstaged, or untracked paths present. `unconfirmed` SHALL represent any inability to authoritatively verify the workspace identity, generation, branch, HEAD, tree, or status, including a probe timeout, identity mismatch, missing generation, or unavailable receipt.

#### Scenario: The workspace is clean after a valid result
- **WHEN** a successful ActionCompletion has a receipt that authoritatively verifies the expected branch, HEAD, tree, workspace generation, and empty status
- **THEN** the outcome SHALL be `committed-clean`
- **AND** the task SHALL be eligible for its normal successful Workflow settlement and advancement

#### Scenario: The workspace remains dirty after a valid result
- **WHEN** a successful ActionCompletion has an authoritative receipt for the expected workspace and the receipt contains staged, unstaged, or untracked paths
- **THEN** the outcome SHALL be `dirty`
- **AND** the Action output, artifacts, receipt evidence, and workspace identity SHALL remain available for recovery
- **AND** the outcome MUST NOT become `TaskFailed`, `StageFailed`, or `WorkflowRunFailed` solely because cleanup did not make the workspace clean

#### Scenario: The workspace cannot be verified
- **WHEN** the Runner cannot authoritatively prove the workspace identity, generation, branch, HEAD, tree, or status for a valid ActionCompletion
- **THEN** the outcome SHALL be `unconfirmed` with the verification reason
- **AND** the task and workspace SHALL remain recoverable
- **AND** the observation MUST NOT be treated as a successful clean commit or as a business task failure

#### Scenario: The Action itself fails conclusively
- **WHEN** the Action returns an authoritative failure independent of cleanup or workspace uncertainty
- **THEN** the Workflow SHALL retain its existing failed-Action semantics
- **AND** the commit boundary MUST NOT manufacture a successful or recoverable-clean outcome from that Action failure

### Requirement: Workflow and Runner settlement contract
A result report SHALL carry the immutable ActionCompletion, matching CommitReceipt, arbitrated workspace outcome, cleanup evidence, and exact execution identity needed for validation. The Workflow boundary SHALL accept a report only when its completion, receipt, outcome, task attempt, work identity, runner identity, workspace identity, and workspace generation match the active execution. A missing or invalid boundary SHALL be rejected or recorded only as a non-settling `WorkflowTaskRecoveryState=unconfirmed` observation with reason `boundary-missing` or a validation reason; it MUST NOT be translated through the legacy plain-WorkResult settlement path. The Server SHALL persist the accepted result before publishing task, stage, run, artifact, variable, or status projections. A `committed-clean` result SHALL use normal successful settlement. `dirty` and `unconfirmed` results SHALL be exposed as explicit recoverable outcomes with a reason, preserved Action output and artifacts, workspace identity, and the next recovery action or deadline. Runtime session activity, transcript text, stop delivery, or cleanup-attempt count MUST NOT substitute for the completion and receipt.

#### Scenario: A clean report settles normally
- **WHEN** the Server receives a matching `committed-clean` report and durably persists its completion and receipt
- **THEN** the Workflow SHALL apply the normal successful task settlement and advancement rules
- **AND** the task output and artifacts SHALL be published exactly once

#### Scenario: A dirty report reaches the settlement boundary
- **WHEN** the Server receives a matching `dirty` report after bounded cleanup is exhausted or deferred
- **THEN** the Workflow SHALL expose a recoverable dirty outcome with the Action output, artifacts, receipt evidence, cleanup evidence, and workspace identity
- **AND** task, stage, and run projections MUST NOT become business failures solely because the workspace is dirty
- **AND** recovery consumers SHALL receive enough identity and scope information to preserve or reclaim the workspace safely

#### Scenario: An unconfirmed report reaches the settlement boundary
- **WHEN** the Server receives a matching `unconfirmed` report because the workspace probe or receipt verification cannot be trusted
- **THEN** the Workflow SHALL expose a recoverable unconfirmed outcome with the reason and recovery deadline or next action
- **AND** the Workflow MUST NOT settle the task as successful or failed from that observation alone

#### Scenario: Session activity disagrees with the durable result
- **WHEN** a runtime session appears idle, complete, stopped, or successful but no matching durable ActionCompletion and CommitReceipt exists
- **THEN** the Workflow SHALL keep the execution unconfirmed and recoverable
- **AND** session activity MUST NOT settle the task

### Requirement: Recoverable Workflow task state and allowed actions
For a valid successful Action whose workspace outcome is `dirty` or `unconfirmed`, the server SHALL persist a `WorkflowTaskRecovery` value on the task attempt. Its state SHALL be exactly `dirty` or `unconfirmed`, and it SHALL include the boundary fingerprint, full execution/workspace identity, reason, preserved output/artifact references, cleanup scope, deadline or next action, and projection progress. The persisted `TaskRun.Status`, containing `StageRun.Status`, and `WorkflowRun.Status` SHALL remain `Running`; the task's runner/workspace assignment SHALL remain fenced until an authorized recovery operation or explicit stop. The wire task, containing stage, and run status SHALL be `recoverable-dirty` or `recoverable-unconfirmed` according to the value, with `Failure=null` and the recovery metadata exposed.

While `WorkflowTaskRecovery` is present, `NextWork` and `PendingWork` SHALL be empty, ordinary task claims/reports, stage advancement, retries, reruns, and runtime task insertion SHALL be rejected or no-ops, and reminders SHALL not turn the state into `TaskFailed`, `StageFailed`, or `WorkflowRunFailed`. The only allowed transitions are a current-fence scoped cleanup, inspection, an idempotent `WorkspaceVerification`, an authorized source-adoption operation, fresh-generation allocation that leaves the old workspace protected, or explicit stop/cancel. A clean verification transitions the same task attempt through the normal idempotent completion path; a dirty/unconfirmed verification appends evidence and retains the corresponding recovery state.

An explicit stop/cancel marks the task `Cancelled`, retains the boundary and recovery value for protected reclamation, marks the run `Stopped`, and emits no task, stage, or run business-failure event. The existing persisted stage enum may remain `Running`; a stopped run's stage wire projection SHALL be `stopped`. No late report or verification may reopen a cancelled task attempt. A conclusive Action failure, including a pre-Action failure represented by the boundary, retains the existing `TaskRun=Failed`, `StageRun=Failed`, and `WorkflowRun=Failed` semantics and is not converted into this recovery state.

#### Scenario: Dirty and unconfirmed outcomes are fenced from normal dispatch
- **WHEN** a matching successful report is accepted with outcome `dirty` or `unconfirmed`
- **THEN** the task, stage, and run remain persisted `Running` with a `WorkflowTaskRecovery` value and no failure details
- **AND** the status projections show the corresponding recoverable wire value with a next action or deadline
- **AND** no worker can claim/advance/retry the task through the ordinary dispatch path

#### Scenario: Later clean verification completes the original attempt
- **WHEN** an authorized `WorkspaceVerification` is authoritative, matches the original workspace identity/generation and expected branch, and reports empty status
- **THEN** the server SHALL mark the recovery settled and apply the existing task completion and advancement path exactly once
- **AND** it SHALL retain the original ActionCompletion and CommitReceipt, publish their output/artifacts once, and create no new attempt

### Requirement: Fenced and bounded cleanup recovery
Cleanup SHALL execute under an idempotent lease and fencing token bound to the exact Workflow run, task attempt, work identity, runner identity, workspace identity, and workspace generation. The lease SHALL have an explicit expiration and bounded work budget. Only the holder of the current fence for the matching generation SHALL be authorized to mutate the workspace. Replaying the same cleanup operation SHALL return its existing result without repeating a mutation, and a stale or expired fence SHALL be rejected without touching the workspace. Same-session cleanup retries MUST NOT be the condition that determines whether a valid Action result is a business success or failure.

#### Scenario: Cleanup acquires the current workspace lease
- **WHEN** cleanup requests a lease for the persisted task and its current workspace generation
- **THEN** the system SHALL grant at most one current fence for that exact identity and generation
- **AND** cleanup SHALL be authorized only while that fence and lease remain valid

#### Scenario: Cleanup is replayed after an acknowledgement is lost
- **WHEN** the same cleanup operation is replayed after its response is lost
- **THEN** the system SHALL return the original lease or cleanup result
- **AND** it MUST NOT start a second cleanup owner or repeat an already-applied mutation

#### Scenario: A stale cleanup worker acts after a new generation is allocated
- **WHEN** a cleanup worker presents a fence for an older workspace generation after the task has been rebound to a newer generation
- **THEN** the system SHALL reject the stale fence
- **AND** the old worker MUST NOT modify either the protected old workspace or the new workspace through that operation

#### Scenario: Cleanup reaches its bound without a clean receipt
- **WHEN** the cleanup lease expires or its work budget is exhausted before an authoritative clean receipt is available
- **THEN** cleanup SHALL stop authorizing mutations
- **AND** the task SHALL retain its `dirty` or `unconfirmed` recoverable outcome
- **AND** the Runner MUST NOT continue unbounded same-session cleanup retries

### Requirement: Cleanup is limited to explicitly scoped generated artifacts
Cleanup SHALL verify the workspace identity and SHALL be permitted to remove only generated artifact paths explicitly included in the persisted cleanup scope for that task and workspace generation. Cleanup SHALL enforce workspace path containment and SHALL preserve task source files, task commits, declared outputs, recorded artifacts, and unrelated user changes. Cleanup MUST NOT use broad `git reset`, `git clean`, checkout, restore, branch replacement, or whole-workspace deletion operations to force a clean status.

Authorized source adoption is a separate `AdoptTaskSourceChanges` operation. It SHALL require an authenticated recovery operator with Workflow permission, a current server-issued fence for the exact boundary identity and workspace generation, an idempotency operation id, and an explicit repository-relative source-path allowlist. The server SHALL reject an allowlist that overlaps generated cleanup paths, declared outputs, or recorded artifacts. The runner SHALL revalidate the marker, registry binding, generation, containment, and current diff, and may run only path-limited add/commit operations for the accepted allowlist. It SHALL never use adoption to reset, clean, checkout, replace a branch, or delete a workspace. Rejection or mutation failure SHALL leave every source, output, artifact, and unrelated path unchanged. A successful operation SHALL record its new task commit and included paths as recovery evidence and SHALL be followed by a `WorkspaceVerification` naming that operation; it SHALL NOT alter the original CommitReceipt.

#### Scenario: An allowed generated artifact is present
- **WHEN** a current cleanup fence identifies a generated temporary path inside the recorded cleanup scope
- **THEN** cleanup SHALL remove only that path
- **AND** task source, task commits, declared outputs, recorded artifacts, and other workspace paths SHALL remain unchanged

#### Scenario: An unscoped task change keeps the workspace dirty
- **WHEN** the workspace contains a task source change or output path outside the explicit cleanup scope
- **THEN** cleanup SHALL leave that path untouched
- **AND** the receipt outcome SHALL remain `dirty` until an authoritative recovery operation produces a `WorkspaceVerification`
- **AND** cleanup MUST NOT revert or delete the task change merely to obtain a clean status

#### Scenario: An operator adopts legal uncommitted task-source changes
- **WHEN** an authorized operator holds the current recovery fence and submits an explicit source-path allowlist for the dirty task
- **THEN** the runner SHALL create at most one path-limited task commit for those paths and record the adoption operation id and resulting commit
- **AND** the operation SHALL be rejected without filesystem mutation if a path is outside the workspace, overlaps cleanup/output/artifact scope, or the fence/generation is stale
- **AND** a subsequent clean `WorkspaceVerification` may settle the preserved completion without changing its original receipt or creating another attempt

#### Scenario: A cleanup path escapes the workspace
- **WHEN** a cleanup request names a path outside the recorded workspace or requests a broad reset, clean, checkout, restore, branch replacement, or workspace deletion
- **THEN** the system SHALL refuse the operation
- **AND** the task SHALL remain recoverable with an actionable cleanup-safety reason
- **AND** no task output or unrelated filesystem content SHALL be removed

### Requirement: Workspace verification is separate from the immutable receipt
A `WorkspaceVerification` SHALL be a distinct mutable recovery observation/operation, not a new or replacement `CommitReceipt`. It SHALL contain a unique idempotency key, the complete original execution identity, the original workspace identity and generation, the immutable boundary fingerprint, observed current branch/HEAD/tree/status/path evidence, authority, reason, verifier/source, and an optional `AdoptTaskSourceChanges` operation id. The server SHALL accept it only for the matching recoverable task and current recovery fence. Replaying the same key with the same canonical payload SHALL return the original observation without another probe, event, or settlement; the same key with a different payload SHALL be rejected. A verification for a fresh workspace generation cannot settle the original completion. A verification may have a different HEAD or tree only when the referenced source-adoption operation was accepted and its resulting commit is the observed descendant of the original boundary HEAD; cleanup-only verification must retain the original branch/HEAD/tree.

#### Scenario: Cleanup produces later clean evidence
- **WHEN** scoped cleanup changes the workspace and an authorized verifier observes the same workspace identity/generation with the original branch/HEAD/tree and empty status
- **THEN** the system SHALL persist a `WorkspaceVerification`, leave the original `CommitReceipt` unchanged, and complete the original task attempt idempotently

#### Scenario: A verification conflicts with its idempotency key
- **WHEN** a retry uses an existing verification key with different identity, generation, branch, HEAD, tree, status, or authority
- **THEN** the server SHALL reject the verification and leave the original recovery and boundary unchanged

### Requirement: Timeout and workspace-generation recovery
When cleanup times out, cleanup fails, the lease expires, or workspace verification is unavailable, the system SHALL preserve the ActionCompletion, CommitReceipt evidence, task identity, and affected workspace for recovery, or SHALL allocate a fresh workspace with a new workspace generation. The system MUST NOT rerun the Action or blindly retry cleanup against an unverified dirty workspace. A fresh generation SHALL receive a new workspace identity and fence, while the prior workspace SHALL remain protected until an explicitly authorized recovery or reclamation operation handles it.

#### Scenario: Cleanup times out on the original workspace
- **WHEN** the bounded cleanup deadline expires before an authoritative clean receipt is available
- **THEN** the original task and workspace SHALL remain recoverable with outcome `dirty` or `unconfirmed`
- **AND** the valid ActionCompletion, receipt evidence, and produced artifacts SHALL remain available
- **AND** the Runner MUST NOT report a terminal business failure solely for the timeout

#### Scenario: Recovery allocates a fresh workspace
- **WHEN** recovery chooses a new workspace instead of continuing against the uncertain original workspace
- **THEN** the new workspace SHALL have a distinct workspace identity, workspace generation, and cleanup fence
- **AND** cleanup operations from the original generation MUST NOT apply to the new workspace
- **AND** the original workspace SHALL remain preserved for inspection or authorized reclamation

#### Scenario: Recovery later verifies the original completion
- **WHEN** an authorized recovery operation submits a `WorkspaceVerification` for the same task and workspace identity, with the original boundary fingerprint, and the verification is `committed-clean`
- **THEN** the system SHALL settle the preserved completion without rerunning the Action
- **AND** the original dirty or unconfirmed observation and immutable CommitReceipt SHALL remain unchanged
- **AND** the verification MUST NOT create a second task attempt or duplicate artifacts

### Requirement: Exact-identity replay and conflict handling
Persistence, initial receipt verification, cleanup, source adoption, WorkspaceVerification, report delivery, report acknowledgement, and Workflow settlement SHALL be idempotent for the exact task attempt and workspace identity. An exact replay of an ActionCompletion, initial CommitReceipt, cleanup operation, source-adoption operation, verification, or result report SHALL return or reuse the existing durable decision without creating duplicate events, artifacts, variable writes, task settlements, status transitions, commits, or workspace mutations. A conflicting completion, initial receipt, outcome, workspace generation, branch, HEAD, tree, or status for the same identity SHALL be rejected and SHALL leave the original durable record unchanged. Later current-state observations belong to WorkspaceVerification and follow its separate identity and idempotency rules.

#### Scenario: A result report is delivered twice
- **WHEN** the same completion, receipt, outcome, and execution identity are delivered again for a task that already accepted that identity
- **THEN** the Server SHALL acknowledge the replay idempotently
- **AND** Workflow history, task output, artifact bindings, variable writes, and status projections SHALL remain unchanged after the first application

#### Scenario: A retry supplies a conflicting completion
- **WHEN** a retry uses the same task and workspace identity but supplies different Action output, error, artifact references, or completion outcome
- **THEN** the Server SHALL reject the conflicting completion
- **AND** it SHALL retain the original completion, receipt, and outcome
- **AND** it MUST NOT settle the task from the conflicting payload

#### Scenario: A retry supplies a conflicting receipt
- **WHEN** a retry uses the same task and workspace identity but supplies a different initial workspace generation, branch, HEAD, tree, or status as the CommitReceipt
- **THEN** the Server SHALL reject the conflicting receipt
- **AND** it SHALL retain the original completion, receipt, and outcome
- **AND** it MUST NOT settle the task from the conflicting payload

#### Scenario: Report acknowledgement is lost after persistence
- **WHEN** the result is durably persisted but the Runner does not receive the report acknowledgement
- **THEN** replay SHALL return the same durable completion, receipt, and outcome
- **AND** the Server SHALL not append another terminal or recoverable transition
- **AND** the Runner SHALL retain its local receipt until an idempotent acknowledgement is received

### Requirement: Consistent runtime coverage and deterministic recovery
The commit boundary SHALL apply identically to generic Workflow Actions and Workflow executions using Pi or OpenCode. No runtime-specific path SHALL settle from an in-memory result, session transcript, cleanup response, or runtime stop response without the matching ActionCompletion and CommitReceipt. The runner tests SHALL exercise workspace setup failure, unknown/removed Action, unresolved/invalid input, start branch-probe failure, Action throw, normalized Action failure, end branch-probe failure, artifact capture failure, output/set-variable failure, outer catch failure, and report serialization/transport failure; every terminal path must either durably persist its boundary or retain its started fence without reporting. Filesystem probes, cleanup lease time, workspace-generation changes, and recovery deadlines SHALL be controllable by deterministic filesystem and clock test doubles so `committed-clean`, `dirty`, `unconfirmed`, lease expiry, replay, and conflict behavior are reproducible.

#### Scenario: A generic Action leaves a dirty workspace
- **WHEN** a generic Action returns a valid result and its authoritative workspace probe reports dirty status
- **THEN** the system SHALL persist the completion and receipt
- **AND** it SHALL expose the same recoverable `dirty` outcome while preserving the same Action output, artifacts, and workspace identity

#### Scenario: Pi or OpenCode leaves cleanup unconfirmed
- **WHEN** a Pi or OpenCode execution returns a valid result but its workspace generation or status cannot be verified
- **THEN** the system SHALL persist the completion and unconfirmed evidence
- **AND** it SHALL use the same recoverable settlement, fencing, timeout, and replay rules as the generic Action path

#### Scenario: Deterministic time and filesystem probes advance
- **WHEN** a test advances a fake clock past the cleanup lease deadline or changes a fake filesystem probe from dirty to clean
- **THEN** the resulting outcome and allowed transitions SHALL be deterministic
- **AND** replaying the same identity under the same fake state MUST NOT produce additional cleanup mutations or Workflow events
