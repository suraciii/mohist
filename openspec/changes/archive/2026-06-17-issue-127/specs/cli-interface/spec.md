## ADDED Requirements

### Requirement: CLI update refreshes managed runner runtime

Full `mo update` SHALL rebuild the runner distribution and restart the managed runner runtime when the runner is installed and manageable. The command SHALL report whether runner refresh was performed or skipped, and skipped runner refresh SHALL include the reason.

#### Scenario: Full update refreshes installed runner
- **WHEN** the user runs `mo update`
- **AND** the local runner is installed and manageable
- **THEN** the CLI SHALL rebuild `packages/runner/dist`
- **AND** the CLI SHALL restart the managed runner service after the build succeeds
- **AND** the CLI output SHALL report that runner build and restart were performed

#### Scenario: Full update explains skipped runner refresh
- **WHEN** the user runs `mo update`
- **AND** runner refresh is skipped because the runner is not installed, not manageable, or not in scope
- **THEN** the CLI output SHALL report that runner refresh was skipped
- **AND** the output SHALL include the skip reason

### Requirement: CLI update verification detects stale runner runtime

Update verification SHALL validate runner runtime identity instead of only checking whether the runner service is active or connected. Verification SHALL fail or report an explicit degraded result when the live runner code identity does not match the current source or rebuilt distribution identity.

#### Scenario: Verification passes for matching runner runtime
- **WHEN** update verification runs after `mo update`
- **AND** the live runner code identity matches the current source or rebuilt `packages/runner/dist`
- **THEN** verification SHALL report the runner runtime as current
- **AND** it SHALL NOT rely only on service active or connected status

#### Scenario: Verification detects stale runner runtime
- **WHEN** update verification runs after `mo update`
- **AND** the runner service is active or connected
- **AND** the live runner code identity does not match the current source or rebuilt `packages/runner/dist`
- **THEN** verification SHALL report stale runner runtime evidence
- **AND** the update result SHALL NOT present runner runtime availability as fully healthy

#### Scenario: Verification records intentional runner skip
- **WHEN** update verification runs after a runner refresh was intentionally skipped
- **THEN** verification SHALL include the skipped runner refresh status and reason
- **AND** it SHALL distinguish intentional skip from a stale live runner mismatch

### Requirement: CLI server-only update is explicit about runner scope

`mo update server` SHALL have explicit server-only semantics. The command SHALL not imply that runner build output or live runner runtime code was refreshed, and SHALL provide clear next-step guidance when runner refresh remains necessary.

#### Scenario: Server-only update reports runner not refreshed
- **WHEN** the user runs `mo update server`
- **THEN** the CLI SHALL update only server-scoped runtime components
- **AND** the CLI output SHALL state that runner build output and runner runtime were not refreshed by this command

#### Scenario: Server-only update gives runner follow-up guidance
- **WHEN** the user runs `mo update server`
- **AND** runner refresh may still be needed for local workflow execution
- **THEN** the CLI output SHALL provide a clear follow-up action for refreshing the runner
- **AND** it SHALL not report overall local runtime freshness as if the runner had been updated
