## MODIFIED Requirements

### Requirement: Auto-done on issue completion

When a linked issue reaches a terminal state — delivered (`done`/`completed`, signalled by `IssueWorkCompleted`) **or** cancelled (`cancelled`, signalled by `IssueClosed`) — the epic SHALL re-evaluate using the same readiness check that backs manual "Mark Done": an epic is ready to complete when it has at least one linked issue and **no open linked issue** (i.e. every linked issue is terminal — `done`/`completed` or `cancelled`). If the readiness condition is satisfied and the epic is in a non-paused, non-terminal state (`idle` or `running`), the epic SHALL automatically transition to `done` without requiring any user action.

The auto-done transition SHALL reuse a single shared readiness computation (no open linked issue) across auto-done, manual "Mark Done", resume, and the detail/list read models, and SHALL NOT introduce divergent definitions of "ready". A `cancelled` linked issue SHALL NOT count toward `deliveredCount`, but SHALL NOT block completion.

The same terminal-event handler SHALL, after the readiness check, drive autonomous advancement for `running` epics (see "Autonomous advancement on terminal issue events").

#### Scenario: All linked issues delivered triggers auto-done

- **WHEN** the last open linked issue of an `idle` or `running` epic transitions to `done` (or `completed`)
- **THEN** the epic SHALL automatically transition to `done`
- **AND** no manual "Mark Done" action SHALL be required

#### Scenario: Mixed done and cancelled linked issues triggers auto-done

- **WHEN** every linked issue of an `idle` or `running` epic is terminal
- **AND** at least one linked issue is `done`/`completed` and at least one linked issue is `cancelled`
- **THEN** the epic SHALL automatically transition to `done`
- **AND** SHALL NOT remain in `idle` or `running` waiting for the cancelled linked issues to be delivered

#### Scenario: Open linked issue blocks auto-done

- **WHEN** a linked issue of an `idle` or `running` epic reaches a terminal state
- **AND** at least one other linked issue is open (`backlog`, `draft`, `in_progress`, `blocked`, or `paused`)
- **THEN** the epic SHALL NOT transition to `done`
- **AND** the epic SHALL remain in its current non-terminal state

#### Scenario: Readiness shared with manual Mark Done

- **WHEN** the auto-done evaluation runs
- **THEN** it SHALL use the same no-open-linked-issue readiness condition as the manual "Mark Done" path
- **AND** SHALL NOT treat `cancelled` issues as delivered for `deliveredCount` purposes
- **AND** SHALL NOT let `cancelled` issues block completion

### Requirement: Paused epic excluded from auto-done

A `paused` epic SHALL NOT automatically transition to `done` or advance the next issue as a result of a linked issue reaching a terminal state. Paused indicates the epic is intentionally not advancing, including automated completion and autonomous progression.

#### Scenario: Paused epic does not auto-done on issue completion

- **WHEN** a linked issue of a `paused` epic reaches a terminal state
- **THEN** the epic SHALL remain `paused`
- **AND** SHALL NOT automatically transition to `done`
- **AND** SHALL NOT start the next linked issue

#### Scenario: Resume re-evaluates readiness and advances

- **WHEN** a `paused` epic is resumed to `running`
- **AND** all linked issues are already terminal (`done`/`completed` or `cancelled`)
- **THEN** the epic SHALL automatically transition to `done` as part of (or immediately after) the resume
- **AND** SHALL NOT remain `running` waiting for a subsequent trigger

#### Scenario: Resume with open issues advances next

- **WHEN** a `paused` epic is resumed to `running`
- **AND** at least one linked issue is open
- **AND** there is a startable next issue
- **THEN** the epic SHALL become `running` and SHALL advance the next startable issue

#### Scenario: Resume with open issues but nothing startable

- **WHEN** a `paused` epic is resumed to `running`
- **AND** at least one linked issue is open
- **AND** no linked issue is startable (e.g. next is `draft` or externally blocked)
- **THEN** the epic SHALL become `running` and remain in running-but-idle until a startable issue appears

### Requirement: Manual Mark Done retained

The manual "Mark Done" capability SHALL remain available for edge cases (e.g. closing early, or recovering from a missed automatic trigger). Its preconditions SHALL use the same no-open-linked-issue readiness rule as auto-done and the read-model `readyToMarkDone` indicator: an epic is ready to Mark Done when it has at least one linked issue and no open linked issue. Its error-handling shape SHALL be unchanged (a clear rejection when the epic is not ready), but the readiness predicate itself SHALL permit epics whose remaining linked issues are all `cancelled`.

#### Scenario: Manual Mark Done works when all linked issues are delivered

- **WHEN** a user invokes "Mark Done" on a non-paused epic whose linked issues are all `done`/`completed`
- **THEN** the epic SHALL transition to `done`, identical to prior behavior

#### Scenario: Manual Mark Done works with mixed done and cancelled linked issues

- **WHEN** a user invokes "Mark Done" on a non-paused epic whose linked issues are all terminal
- **AND** at least one linked issue is `cancelled`
- **THEN** the epic SHALL transition to `done`
- **AND** SHALL NOT be rejected on the basis of undelivered linked issues

#### Scenario: Manual Mark Done rejected while open linked issues remain

- **WHEN** a user invokes "Mark Done" on an epic that has at least one open linked issue
- **THEN** the request SHALL be rejected with a clear error
- **AND** the epic SHALL remain in its current state

#### Scenario: Auto-done does not block manual Mark Done

- **WHEN** an epic has already auto-transitioned to `done`
- **AND** a user attempts manual "Mark Done"
- **THEN** the request SHALL behave the same as invoking "Mark Done" on an already-`done` epic today (e.g. no-op or terminal-state rejection, per existing behavior)

### Requirement: Epic lifecycle state machine

An epic SHALL have the lifecycle states `idle`, `running`, `paused`, `done`, and `closed`. `idle` ("exists, not yet started") replaces the prior `active` state. The state machine transitions are:

- `create` → `idle`
- `idle` --Start--> `running` (and immediately attempt to advance the next startable issue)
- `running` --Pause--> `paused`
- `paused` --Resume--> `running` (and re-evaluate readiness / advancement)
- `running` --no open linked issue (every linked issue terminal: `done`/`completed` or `cancelled`)--> `done`
- any non-terminal state --Close--> `closed`

The `running` → `done` transition condition is "no open linked issue", shared verbatim by auto-done, manual "Mark Done", resume re-evaluation, and the read-model `readyToMarkDone` indicator. A `cancelled` linked issue is terminal and SHALL NOT block this transition; it SHALL NOT count toward `deliveredCount`.

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

#### Scenario: Running epic with no open linked issue becomes done

- **WHEN** a `running` epic has at least one linked issue and no open linked issue (every linked issue is `done`/`completed` or `cancelled`)
- **THEN** the epic SHALL transition to `done`
- **AND** SHALL NOT be required to have every linked issue delivered

#### Scenario: Close from any non-terminal state

- **WHEN** a Close is invoked on an epic in `idle`, `running`, or `paused`
- **THEN** the epic SHALL transition to `closed`
- **AND** SHALL NOT advance any further linked issues

#### Scenario: Terminal state ignores lifecycle transitions

- **WHEN** Start, Pause, or Resume is invoked on a `done` or `closed` epic
- **THEN** the request SHALL be rejected as a terminal-state violation (or be a no-op per the idempotency rule)
- **AND** the epic SHALL remain in its terminal state

### Requirement: Resume epic autonomous progression

The `mo epic resume {n}` command (and equivalent HTTP API / Web UI action) SHALL transition a `paused` epic to `running` and re-evaluate: if the epic has no open linked issue (every linked issue terminal), auto-done applies; otherwise the next startable linked issue SHALL be advanced. Resume SHALL be idempotent.

#### Scenario: Resume advances next startable issue

- **WHEN** `mo epic resume` is invoked on a `paused` epic with at least one open linked issue and a startable next issue
- **THEN** the epic SHALL transition to `running`
- **AND** the next startable linked issue SHALL be advanced

#### Scenario: Resume is idempotent

- **WHEN** `mo epic resume` is invoked on an epic that is already `running`
- **THEN** the request SHALL be a no-op
- **AND** SHALL NOT error
- **AND** SHALL NOT re-trigger advancement

### Requirement: Autonomous advancement on terminal issue events

A `running` epic SHALL autonomously advance its linked issues: whenever one of its linked issues reaches a terminal state (`done`/`completed` or `cancelled`), the epic SHALL reconcile — first applying the auto-done readiness check (no open linked issue), and if not satisfied, attempting to advance the next startable linked issue. The reconcile SHALL be triggered for both terminal events because both clear the single in-progress slot that the serial rule is waiting on.

An `idle` epic SHALL NOT autonomously advance (it is not self-driving); terminal events on an `idle` epic's linked issues SHALL only trigger the auto-done readiness check, never advancement.

#### Scenario: Done issue advances next

- **WHEN** a linked issue of a `running` epic transitions to `done`/`completed`
- **AND** at least one other linked issue is open
- **AND** a next linked issue is startable
- **THEN** the epic SHALL remain `running`
- **AND** SHALL advance the next startable linked issue

#### Scenario: Cancelled in-progress issue advances next

- **WHEN** the in-progress linked issue of a `running` epic is cancelled (clearing the single in-progress slot)
- **AND** at least one other linked issue is open and startable
- **THEN** the epic SHALL reconcile
- **AND** SHALL advance the next startable linked issue (cancel is treated as "removed from scope", not as an execution failure)
- **AND** SHALL NOT get stuck waiting for the cancelled slot

#### Scenario: Cancelled non-in-progress issue is skipped

- **WHEN** a linked issue of a `running` epic that is not the in-progress issue is cancelled
- **THEN** the epic SHALL reconcile
- **AND** the cancelled issue SHALL be excluded from future selection
- **AND** advancement SHALL proceed per the serial rule as before

#### Scenario: Failed issue holds the epic

- **WHEN** the in-progress linked issue of a `running` epic fails (stays `in_progress`, health `blocked`) and does not reach a terminal state
- **THEN** the epic SHALL NOT advance another linked issue (the single in-progress slot is occupied)
- **AND** the epic SHALL remain `running`
- **AND** the dashboard SHALL surface the blocked status and reason
- **AND** no epic-level failure/retry state SHALL be introduced

#### Scenario: Idle epic does not autonomously advance

- **WHEN** a linked issue of an `idle` epic reaches a terminal state
- **AND** at least one linked issue is open
- **THEN** the epic SHALL remain `idle`
- **AND** SHALL NOT advance any linked issue
