## MODIFIED Requirements

### Requirement: Non-eligible workspaces are never auto-cleaned

The runner MUST NOT automatically remove any workspace whose registry entry is `active`, or whose workflow run is in a non-terminal state (`created`, `pending`, `ready`, `running`, `paused`, `awaiting approval`), or whose identity is missing or mismatched. Automatic cleanup applies exclusively to `eligible` entries that pass the pre-delete safety guards.

#### Scenario: Active workspace is never removed by automatic cleanup

- **WHEN** a registry entry is `active`
- **THEN** the runner MUST NOT remove that workspace via retention or budget eviction

#### Scenario: Awaiting-approval workspace is never removed by automatic cleanup

- **WHEN** the owning workflow run is in an `awaiting approval` state
- **THEN** the runner MUST NOT remove that workspace via automatic cleanup

#### Scenario: Pending or Ready workspace is never removed by automatic cleanup

- **WHEN** the owning workflow run is in a `pending` (unassigned, waiting for claim) state
- **THEN** the runner MUST NOT remove that workspace via automatic cleanup
- **WHEN** the owning workflow run is in a `ready` (assigned, waiting for pickup) state
- **THEN** the runner MUST NOT remove that workspace via automatic cleanup

#### Scenario: Created workspace is never removed by automatic cleanup

- **WHEN** the owning workflow run is in a `created` (built, not started) state
- **THEN** the runner MUST NOT remove that workspace via automatic cleanup
