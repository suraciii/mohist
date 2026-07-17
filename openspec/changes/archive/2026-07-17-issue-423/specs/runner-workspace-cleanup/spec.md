### Requirement: Guard-aborted eligible workspaces are resolved, not retried indefinitely

When the cleanup loop's automatic removal of an `eligible` workspace is refused by a pre-delete safety guard — because the resolved target path is outside `runnerRoot`, the workspace marker (`.mohist/workspace.json`) is missing or unreadable, or the marker's `workflowRunId` does not match the registry entry's `workflowRunId` — the runner SHALL resolve the registry entry so the cleanup loop no longer selects it as an `eligible` candidate on subsequent ticks and no longer emits the refusal warning for it. A guard refusal is deterministic (it yields an identical outcome on every tick), so the runner MUST NOT retry it indefinitely. Resolution SHALL occur independently of the retention and budget policy: a stuck entry SHALL be resolved even when both policies are disabled. Resolving the entry MUST NOT delete the workspace directory — the safety refusal stands; only the registry's repeated re-evaluation ends. A guard refusal is the only trigger for resolution; an `eligible` workspace that passes the guards SHALL still be removed and deregistered normally.

#### Scenario: Missing or unreadable marker resolves the entry and leaves the directory intact

- **WHEN** the cleanup loop attempts to remove an `eligible` workspace whose marker is missing or unreadable
- **THEN** the runner SHALL resolve the registry entry so it is no longer selected as `eligible`
- **AND** the runner MUST NOT delete the workspace directory

#### Scenario: Marker workflowRunId mismatch resolves the entry and leaves the directory intact

- **WHEN** the cleanup loop attempts to remove an `eligible` workspace whose marker `workflowRunId` does not match the registry entry's `workflowRunId`
- **THEN** the runner SHALL resolve the registry entry so it is no longer selected as `eligible`
- **AND** the runner MUST NOT delete the workspace directory

#### Scenario: Path outside runnerRoot resolves the entry and leaves the directory intact

- **WHEN** the cleanup loop attempts to remove an `eligible` workspace whose resolved path is outside `runnerRoot`
- **THEN** the runner SHALL resolve the registry entry so it is no longer selected as `eligible`
- **AND** the runner MUST NOT delete the workspace directory

#### Scenario: A resolved entry is not re-attempted or re-warned on subsequent ticks

- **WHEN** a guard-aborted `eligible` workspace has been resolved on a prior cleanup tick
- **THEN** the next cleanup tick SHALL NOT re-enter the removal path for that entry
- **AND** SHALL NOT re-emit the per-entry refusal warning for it

#### Scenario: Resolution occurs even when both retention and budget are disabled

- **WHEN** an `eligible` workspace's removal is refused by a guard
- **AND** both `retentionDays` and `storageBudgetBytes` are configured as disabled (`<= 0` / unlimited)
- **THEN** the runner SHALL still resolve the entry on that tick
- **AND** subsequent ticks SHALL perform no per-entry work for that entry

### Requirement: Stuck-entry resolution is persisted and survives runner restart

The resolution of a guard-aborted entry SHALL be written through the runner-local registry's atomic write-through persistence (the same mechanism used for every registry mutation), so a resolved entry does not reappear as `eligible` after a runner restart.

#### Scenario: A resolved entry does not reappear as eligible after restart

- **WHEN** the runner restarts after resolving a guard-aborted entry
- **THEN** the entry SHALL NOT be reloaded as `eligible`
- **AND** the cleanup loop SHALL NOT re-evaluate or re-emit the refusal warning for that entry post-restart
