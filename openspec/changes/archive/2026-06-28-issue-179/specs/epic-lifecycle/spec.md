## MODIFIED Requirements

### Requirement: Epic lifecycle state machine

An epic SHALL have the lifecycle states `idle`, `running`, `paused`, `done`, and `closed`. `idle` ("exists, not yet started") replaces the prior `active` state. The state machine transitions are:

- `create` → `idle`
- `idle` --Start--> `running` (and immediately attempt to advance the next startable issue)
- `running` --Pause--> `paused`
- `paused` --Resume--> `running` (and re-evaluate readiness / advancement)
- `running` --all linked issues delivered (#177)--> `done`
- any non-terminal state --Close--> `closed` (non-destructive: the epic's linked-issue memberships SHALL be retained so that membership history and progress remain readable)

The Close transition SHALL be non-destructive: it SHALL only mark the epic terminal and halt further advancement, and SHALL NOT unlink, remove, or clear any of the epic's linked-issue memberships. Removal of a single membership is available only via the explicit unlink operation (see `epic-issue-membership`).

The "at most one in-progress linked issue" rule is an execution-plane capacity policy (N=1), not an aggregate invariant; it SHALL be expressed in a way that leaves room for future multi-runner parallelism.

Existing epic data in the legacy `active` status SHALL be migrated to `idle` on upgrade. Post-migration behavior SHALL NOT regress: legacy `active` epics were not self-driving, and `idle` epics are not self-driving until explicitly started.

#### Scenario: Newly created epic is idle
- **WHEN** an epic is created
- **THEN** its initial state SHALL be `idle`
- **AND** it SHALL NOT autonomously start any linked issue

#### Scenario: Legacy active epics migrate to idle
- **WHEN** the system upgrades to the new state model
- **THEN** every existing epic previously in `active` SHALL be migrated to `idle`
- **AND** no autonomous advancement SHALL begin without an explicit Start

#### Scenario: Close from any non-terminal state preserves links
- **WHEN** a Close is invoked on an epic in `idle`, `running`, or `paused`
- **THEN** the epic SHALL transition to `closed`
- **AND** SHALL NOT advance any further linked issues
- **AND** SHALL NOT remove or clear any of its linked-issue memberships
- **AND** the membership set present before close SHALL remain intact and readable afterward

#### Scenario: Close is non-destructive
- **WHEN** an epic with linked issues is closed
- **THEN** no linked-issue membership SHALL be unlinked as a side-effect of the close
- **AND** removal of a membership SHALL require an explicit unlink

#### Scenario: Terminal state ignores lifecycle transitions
- **WHEN** Start, Pause, or Resume is invoked on a `done` or `closed` epic
- **THEN** the request SHALL be rejected as a terminal-state violation (or be a no-op per the idempotency rule)
- **AND** the epic SHALL remain in its terminal state
