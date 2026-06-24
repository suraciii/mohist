## ADDED Requirements

### Requirement: Auto-done on issue completion

When a linked issue transitions to a completed state (`done` or `completed`), the epic SHALL re-evaluate its readiness using the same readiness check that backs manual "Mark Done" (all linked issues complete). If the readiness condition is satisfied and the epic is in the `active` state, the epic SHALL automatically transition to `done` without requiring any user action.

The auto-done transition SHALL reuse the existing readiness computation (`EpicProgress.IsCompleted` across all linked issues) and SHALL NOT introduce a new definition of "ready".

#### Scenario: All linked issues complete triggers auto-done
- **WHEN** the last linked issue of an `active` epic transitions to `done` (or `completed`)
- **THEN** the epic SHALL automatically transition from `active` to `done`
- **AND** no manual "Mark Done" action SHALL be required

#### Scenario: Partial completion does not auto-done
- **WHEN** a linked issue of an `active` epic transitions to `done` but at least one other linked issue is not yet complete
- **THEN** the epic SHALL remain `active`

#### Scenario: Readiness check unchanged
- **WHEN** the auto-done evaluation runs
- **THEN** it SHALL use the same readiness condition as the manual "Mark Done" path
- **AND** SHALL NOT treat `cancelled` issues as complete

#### Scenario: Cancelled issue prevents auto-done
- **WHEN** all linked issues are either complete (`done`/`completed`) or `cancelled`, and at least one linked issue is `cancelled`
- **THEN** the epic SHALL NOT automatically transition to `done`
- **AND** its behavior SHALL match the existing manual "Mark Done" behavior for such epics

### Requirement: Paused epic excluded from auto-done

A `paused` epic SHALL NOT automatically transition to `done` as a result of issue completion. Paused indicates the epic is intentionally not advancing, including automated completion.

#### Scenario: Paused epic does not auto-done on issue completion
- **WHEN** a linked issue of a `paused` epic transitions to `done`
- **THEN** the epic SHALL remain `paused`
- **AND** SHALL NOT automatically transition to `done`

#### Scenario: Resume re-evaluates readiness
- **WHEN** a `paused` epic is resumed to `active`
- **AND** all linked issues are already complete (`done`/`completed`)
- **THEN** the epic SHALL automatically transition to `done` as part of (or immediately after) the resume
- **AND** SHALL NOT remain `active` waiting for a subsequent trigger

#### Scenario: Resume with incomplete issues stays active
- **WHEN** a `paused` epic is resumed to `active`
- **AND** at least one linked issue is not complete
- **THEN** the epic SHALL become `active` and SHALL NOT auto-done

### Requirement: Manual Mark Done retained

The manual "Mark Done" capability SHALL remain available for edge cases (e.g. closing early, or recovering from a missed automatic trigger). Its behavior, preconditions, and error cases SHALL be unchanged.

#### Scenario: Manual Mark Done still works
- **WHEN** a user invokes "Mark Done" on an epic that is ready (all linked issues complete) and not paused
- **THEN** the epic SHALL transition to `done`, identical to prior behavior

#### Scenario: Auto-done does not block manual Mark Done
- **WHEN** an epic has already auto-transitioned to `done`
- **AND** a user attempts manual "Mark Done"
- **THEN** the request SHALL behave the same as invoking "Mark Done" on an already-`done` epic today (e.g. no-op or terminal-state rejection, per existing behavior)

### Requirement: Reliable and idempotent auto-done trigger

The mechanism that signals issue completion to the owning epic SHALL be idempotent and race-tolerant. Duplicate or reordered completion signals SHALL NOT cause incorrect epic state (e.g. double-transition, stuck state).

#### Scenario: Duplicate completion signal is safe
- **WHEN** the same issue-completion signal is delivered more than once to an epic that is already `done`
- **THEN** the epic SHALL remain `done`
- **AND** SHALL NOT error or change state

#### Scenario: Out-of-order completion signals converge
- **WHEN** completion signals for multiple issues arrive in any order
- **AND** all linked issues are complete
- **THEN** the epic SHALL end in `done`

#### Scenario: Terminal epic ignores completion signals
- **WHEN** an issue-completion signal targets an epic that is already terminal (`done` or `closed`)
- **THEN** the epic SHALL remain in its terminal state and SHALL NOT error
