### Requirement: Runner maintains an active workspace registry

The runner SHALL maintain a local registry of workspaces it has materialized. Each registry entry SHALL record `issueId`, `issueNumber`, `workflowRunId`, `materializedAt`, the resolved workspace path, a lifecycle phase (`active` or `eligible`), and `terminalAt` once the owning workflow run reaches a terminal state. The registry is runner-local runtime state; it is NOT domain truth and MUST NOT be treated as the source of workflow run lifecycle facts.

- **WHEN** the runner successfully materializes a workspace for a workflow run
- **THEN** the runner SHALL add a registry entry in the `active` phase with `materializedAt` set to the materialization time
- **AND** the entry SHALL be persisted to runner-local state so it survives runner restart

#### Scenario: Registry entry is created on successful materialization

- **WHEN** the runner materializes a workspace for workflow run `wr_123` on issue `42`
- **THEN** a registry entry keyed by `workflowRunId = wr_123` SHALL exist in the `active` phase
- **AND** the entry SHALL record `issueId`, `issueNumber = 42`, `workflowRunId = wr_123`, the workspace path, and `materializedAt`

#### Scenario: Registry survives runner restart

- **WHEN** the runner restarts after having materialized an active workspace
- **THEN** the previously recorded registry entries SHALL be reloaded from runner-local state
- **AND** entries that were `active` SHALL remain `active` until a terminal transition is observed

### Requirement: Workspace marker stays identity-only

The on-disk workspace marker (`.mohist/workspace.json`) SHALL contain only the identity fields `issueId`, `issueNumber`, and `workflowRunId`. The runner MUST NOT write `createdAt`, `finishedAt`, `lastSeenAt`, or any other derived lifecycle field into the marker. Lifecycle timestamps (`materializedAt`, `terminalAt`) live exclusively in the runner-local registry.

#### Scenario: Marker written on materialization contains only identity fields

- **WHEN** the runner materializes a workspace
- **THEN** the marker file at `<workspacePath>/.mohist/workspace.json` SHALL contain exactly `issueId`, `issueNumber`, and `workflowRunId`
- **AND** the marker MUST NOT contain any timestamp field

### Requirement: Terminal state transitions a workspace to cleanup-eligible

The runner SHALL transition a registry entry from `active` to `eligible` when its owning workflow run reaches a terminal state (`completed`, `stopped`, or `failed`). Terminal-state detection SHALL be driven primarily by workflow run lifecycle events delivered to the runner.

#### Scenario: Workflow run completed event marks workspace eligible

- **WHEN** the runner receives a workflow run lifecycle event indicating `wr_123` reached `completed`
- **AND** a registry entry for `wr_123` exists in the `active` phase
- **THEN** the entry SHALL transition to `eligible`
- **AND** `terminalAt` SHALL be set to the time the terminal state was observed

#### Scenario: Already-terminal workspace is not re-transitioned

- **WHEN** the runner receives a terminal event for `wr_123` whose entry is already `eligible`
- **THEN** the entry SHALL remain `eligible`
- **AND** `terminalAt` SHALL NOT be overwritten

### Requirement: Convergence backstop for missed terminal events

On runner startup, reconnect, or when an event may have been missed, the runner SHALL reconcile the `active` phase by querying the server ONLY for registry entries still marked `active`. The runner MUST NOT scan the server's full workflow history or re-query already-`eligible` entries. An entry whose server-reported workflow run is terminal SHALL transition to `eligible`.

#### Scenario: Missed terminal event is recovered on reconnect

- **WHEN** the runner reconnects after missing a terminal event for `wr_123`
- **AND** the registry entry for `wr_123` is still `active`
- **THEN** the runner SHALL query the server for the status of `wr_123` only
- **AND** if the server reports `wr_123` as terminal, the entry SHALL transition to `eligible` with `terminalAt` set

#### Scenario: Convergence does not scan full workflow history

- **WHEN** the runner performs a convergence pass
- **THEN** the runner SHALL query the server exclusively for `active` registry entries
- **AND** the runner MUST NOT enumerate or query workflow runs that have no active registry entry on this runner

### Requirement: Retention policy evicts aged eligible workspaces

The runner SHALL automatically remove an `eligible` workspace once it has been eligible longer than the configured retention window. Retention is measured from `terminalAt`.

#### Scenario: Eligible workspace past retention window is removed

- **WHEN** a registry entry is `eligible` with `terminalAt` older than the configured retention window
- **THEN** the runner SHALL remove the workspace directory
- **AND** the registry entry SHALL be removed

#### Scenario: Eligible workspace within retention window is kept

- **WHEN** a registry entry is `eligible` with `terminalAt` within the configured retention window
- **THEN** the runner MUST NOT remove the workspace

#### Scenario: Retention disabled keeps eligible workspaces

- **WHEN** the retention window is configured as disabled/unlimited
- **THEN** the runner MUST NOT remove any workspace solely on age grounds

### Requirement: Storage budget evicts earliest-eligible workspaces first

When runner workspace usage exceeds the configured storage budget, the runner SHALL evict `eligible` workspaces starting from the one with the earliest `terminalAt`, continuing until usage drops below the target watermark. The runner MUST NOT evict `active` workspaces to satisfy the budget.

#### Scenario: Budget breach evicts earliest eligible workspace first

- **WHEN** runner workspace usage exceeds the configured storage budget
- **AND** eligible workspaces exist with `terminalAt` values T1 < T2 < T3
- **THEN** the runner SHALL evict the workspace with `terminalAt = T1` first
- **AND** SHALL continue evicting in ascending `terminalAt` order until usage is below the target watermark

#### Scenario: Budget cannot evict active workspaces

- **WHEN** runner workspace usage exceeds the storage budget
- **AND** no `eligible` workspaces remain
- **THEN** the runner MUST NOT evict any `active` workspace
- **AND** the runner SHALL leave active workspaces untouched

### Requirement: Pre-delete safety guards on every automatic removal

Before performing any automatic workspace removal, the runner SHALL verify BOTH that the resolved target path is contained under `runnerRoot` AND that the on-disk marker's `workflowRunId` matches the registry entry's `workflowRunId`. If either check fails, the runner MUST abort the removal and leave the directory intact.

#### Scenario: Path outside runnerRoot is refused

- **WHEN** the runner attempts an automatic removal whose resolved target path is not under `runnerRoot`
- **THEN** the runner MUST abort the removal
- **AND** the directory MUST NOT be deleted

#### Scenario: Marker workflowRunId mismatch is refused

- **WHEN** the runner attempts an automatic removal
- **AND** the on-disk marker's `workflowRunId` does not match the registry entry's `workflowRunId`
- **THEN** the runner MUST abort the removal
- **AND** the directory MUST NOT be deleted

#### Scenario: Missing marker is refused

- **WHEN** the runner attempts an automatic removal
- **AND** no marker file exists at the expected path
- **THEN** the runner MUST abort the removal
- **AND** the directory MUST NOT be deleted

### Requirement: Non-eligible workspaces are never auto-cleaned

The runner MUST NOT automatically remove any workspace whose registry entry is `active`, or whose workflow run is in a non-terminal state (`pending`, `paused`, `awaiting approval`, `running`), or whose identity is missing or mismatched. Automatic cleanup applies exclusively to `eligible` entries that pass the pre-delete safety guards.

#### Scenario: Active workspace is never removed by automatic cleanup

- **WHEN** a registry entry is `active`
- **THEN** the runner MUST NOT remove that workspace via retention or budget eviction

#### Scenario: Awaiting-approval workspace is never removed by automatic cleanup

- **WHEN** the owning workflow run is in an `awaiting approval` state
- **THEN** the runner MUST NOT remove that workspace via automatic cleanup

### Requirement: Automatic cleanup scope is limited to workspace directories

Automatic cleanup SHALL remove only the workspace directory under `runnerRoot`. The runner MUST NOT delete workflow runs, issues, events, artifacts, sessions, database records, or the repo cache. Archive-issued triggers and directory-mtime-based completion inference MUST NOT be used.

#### Scenario: Automatic cleanup removes only the workspace directory

- **WHEN** the runner automatically cleans a workspace
- **THEN** only the workspace directory under `runnerRoot` SHALL be deleted
- **AND** no workflow run, issue, event, artifact, session, database record, or repo cache SHALL be deleted

### Requirement: Manual cleanup entry remains unchanged

The existing manual cleanup path (`POST /issues/{N}/cleanup` → runner `RemoveWorkspace`) SHALL continue to be available with its existing user-facing semantics. Automatic cleanup is an additional runner-side mechanism and MUST NOT alter the behavior, guards, or response of the manual entry.

#### Scenario: Manual cleanup still works alongside automatic cleanup

- **WHEN** a user invokes the existing manual cleanup for an issue
- **THEN** the manual cleanup SHALL behave exactly as before this change
- **AND** the presence of automatic cleanup SHALL NOT change its response or guards