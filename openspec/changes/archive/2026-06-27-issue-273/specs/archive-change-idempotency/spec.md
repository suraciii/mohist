## ADDED Requirements

### Requirement: Idempotent archive directory naming across retries

The `mohist/archive-change` action SHALL make its directory move idempotent across retries and reruns. Before moving the change directory into the archive, the action SHALL persist the computed archive directory name to a workflow runtime variable. On any retry or rerun, the action SHALL read that persisted variable and reuse the same archive directory name, so that the already-archived directory is located instead of computing a fresh name. The action SHALL NOT derive the archive directory name from the current wall-clock date alone across a retry boundary, because a cross-day retry would otherwise compute a different prefix and fail with `missing-source` once the source directory has already been moved. This requirement SHALL apply to every workflow profile that uses `mohist/archive-change` (both `mohist/github-pr` and `mohist/default`).

#### Scenario: Archive name persisted before the move

- **WHEN** `mohist/archive-change` computes the archive directory name for the first time
- **THEN** the action SHALL persist that name to a workflow runtime variable before moving the source change directory
- **AND** the directory move SHALL occur only after the name has been persisted

#### Scenario: Retry reuses persisted archive name

- **WHEN** `mohist/archive-change` is retried after a prior execution persisted the archive directory name
- **THEN** the action SHALL read the persisted archive directory name from the workflow runtime variable
- **AND** SHALL reuse that exact name rather than recomputing it from the current date

#### Scenario: Cross-day retry finds the archived directory

- **WHEN** a first execution moved the change directory into the archive on day N, and a retry or rerun executes on day N+1
- **THEN** the action SHALL reuse the name persisted on day N
- **AND** SHALL locate the already-archived directory
- **AND** SHALL NOT fail with `missing-source`

#### Scenario: Applies to all profiles using archive-change

- **WHEN** either the `mohist/github-pr` or `mohist/default` profile archives a change
- **THEN** the `mohist/archive-change` action SHALL exhibit the same idempotent archive-naming behavior

### Requirement: Mid-execution workflow runtime variable writes

The runner action infrastructure SHALL allow an action to programmatically write workflow runtime variables during execution, before its task completes. This mid-execution write is distinct from the declarative `setVars` mechanism, which only patches runtime variables after a task succeeds. A mid-execution variable write SHALL be persisted to the server immediately, reusing the run-variable patch path, so that a subsequent retry or rerun of the same task observes the value even when the current execution fails after the write. The `mohist/archive-change` action SHALL use this capability to persist its archive directory name before performing the directory move.

#### Scenario: Action writes a runtime variable mid-execution

- **WHEN** an action writes a workflow runtime variable during execution, before the task completes
- **THEN** the value SHALL be persisted to the server immediately via the run-variable patch path
- **AND** the write SHALL NOT be deferred until task completion

#### Scenario: Mid-execution write survives task failure

- **WHEN** an action writes a runtime variable mid-execution and the task subsequently fails
- **THEN** a retry or rerun of that task SHALL observe the value written before the failure
