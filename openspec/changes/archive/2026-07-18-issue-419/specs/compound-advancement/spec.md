### Requirement: Starting a parent issue SHALL trigger composite advancement

A start command on an issue that currently has one or more children SHALL fire composite advancement instead of being rejected. Composite advancement SHALL drive the parent's delivery by starting its children; the parent itself SHALL NOT acquire a workflow run. The start command SHALL succeed when at least one child is startable, and SHALL succeed (as a no-op fan-out) when no child is currently startable so that the parent can still enter its aggregated in-progress state and pick up children on the next re-evaluation.

#### Scenario: Starting a parent starts every startable child in parallel
- **WHEN** a parent P has three children A, B, C, all Backlog with satisfied prerequisites, and a caller runs `mo issue start P`
- **THEN** A, B, and C SHALL each start their own workflow run in their own target repository, the starts SHALL proceed in parallel, and P SHALL NOT acquire a workflow run id

#### Scenario: Starting a parent with no currently startable children still enters in-progress
- **WHEN** parent P has children A and B, both Backlog but blocked by unsatisfied prerequisites, and a caller runs `mo issue start P`
- **THEN** the start SHALL succeed, no child SHALL start, P SHALL enter in-progress via aggregation, and P SHALL re-evaluate its children when the blockers clear

#### Scenario: Starting a parent that has zero children behaves like a normal issue start
- **WHEN** an issue has no children (it is not a parent), and a caller runs `mo issue start` on it
- **THEN** the issue SHALL start its own workflow run as a normal issue, and composite advancement SHALL NOT engage

### Requirement: Composite advancement SHALL start only currently startable children

A child is startable for composite advancement when it is in Backlog, is not a draft, has every prerequisite delivered (each prerequisite issue is Done), and has a target repository that is currently declared by the project. Composite advancement SHALL skip any child that does not meet all of these conditions, SHALL NOT abort sibling starts because of a skipped child, and SHALL leave skipped children in Backlog for a later re-evaluation.

#### Scenario: A child with an unsatisfied prerequisite is skipped while its sibling starts
- **WHEN** parent P has children A (no prerequisite) and B (prerequisite is A, A still Backlog), and a caller runs `mo issue start P`
- **THEN** A SHALL start its workflow, B SHALL remain Backlog, and B SHALL NOT block A's start

#### Scenario: A draft child is skipped
- **WHEN** parent P has children A (draft=true) and B (draft=false), both otherwise startable, and a caller runs `mo issue start P`
- **THEN** B SHALL start its workflow and A SHALL remain a Backlog draft

#### Scenario: A child whose target repository is no longer declared is skipped without aborting siblings
- **WHEN** parent P has children A and B, B's target repository has been removed from the project, A is otherwise startable, and a caller runs `mo issue start P`
- **THEN** A SHALL start its workflow, B SHALL remain Backlog, and the failure to resolve B's repository SHALL NOT abort the start of A or of P

### Requirement: Each child SHALL pass the same start gates as a direct manual start

Composite advancement SHALL NOT bypass any per-child start gate. The draft, prerequisite, target-repository-resolution, and runner-dispatch capacity gates that apply to `mo issue start <child>` SHALL apply identically to a child started by composite advancement. A child that cannot acquire a runner slot at this time SHALL remain Backlog and SHALL be retried on the next composite re-evaluation; this SHALL NOT be reported as a failure of the parent start.

#### Scenario: A child without a spare runner slot remains Backlog and the parent still enters in-progress
- **WHEN** parent P has children A, B, C all startable, the project has one runner slot free, and a caller runs `mo issue start P`
- **THEN** exactly one child SHALL acquire the slot and start, the other two SHALL remain Backlog, and P SHALL still enter in-progress

#### Scenario: A per-child gate violation does not fail the parent
- **WHEN** parent P has children A (eligible) and B (currently not eligible for any per-child reason), and a caller runs `mo issue start P`
- **THEN** A SHALL start, B SHALL be skipped, and the start of P SHALL succeed

### Requirement: Composite advancement SHALL continue automatically as children change state

Whenever a child of a parent reaches a terminal state (Done or Cancelled), or a child returns to Backlog via reopen, or a new child is attached, the parent SHALL re-evaluate its remaining non-terminal children and SHALL start any that have become startable. This re-evaluation SHALL continue, without user intervention, until every child of the parent is in a terminal state.

#### Scenario: A child whose prerequisite is a sibling starts when the sibling completes
- **WHEN** parent P has children A and B, B's prerequisite is A, A transitions to Done, and B is Backlog
- **THEN** B SHALL start its workflow automatically as a result of A's completion, with no user action

#### Scenario: A child cancellation triggers re-evaluation of remaining children
- **WHEN** parent P has children A (Backlog, awaiting a runner slot) and B (in-progress), and B is cancelled
- **THEN** P SHALL re-evaluate its remaining Backlog children and SHALL start A if a slot has become available

#### Scenario: Attaching a new child to an in-progress parent triggers re-evaluation
- **WHEN** parent P is in-progress via composite advancement, and a new child C is attached to P via `mo issue update C --parent P`
- **THEN** P SHALL re-evaluate its children and SHALL start C if C is currently startable

### Requirement: A parent SHALL NOT own a workflow run or workflow control surface

A parent SHALL NOT acquire a workflow run id at any point. Workflow control operations (retry, rerun, force-stop, resume, approval) SHALL be rejected when invoked on a parent, because the parent has no workflow. All such operations for the parent's delivery SHALL occur on its children.

#### Scenario: A parent never references a workflow run
- **WHEN** composite advancement runs for parent P, including all subsequent re-evaluations
- **THEN** P's workflow run id SHALL remain null at every observation

#### Scenario: Workflow control operations on a parent are rejected
- **WHEN** a caller attempts any workflow control operation (retry, rerun, force-stop, resume, approval) directly against parent P
- **THEN** the operation SHALL be rejected because P has no workflow, and the caller SHALL be directed to perform the operation on the relevant child

### Requirement: Manual single-child starts SHALL remain available alongside composite advancement

A user MAY start any individual child of a parent directly via `mo issue start <child>` at any time, independent of whether composite advancement has run, is running, or has not yet been invoked. A direct child start SHALL follow the normal single-issue start path and SHALL NOT conflict with composite advancement.

#### Scenario: A user can start a child manually before starting the parent
- **WHEN** parent P has children A and B, neither has been started, and a caller runs `mo issue start A`
- **THEN** A SHALL start its workflow via the normal single-issue start path, P SHALL enter in-progress via aggregation, and B SHALL remain Backlog

#### Scenario: A user can start a child manually while composite advancement is mid-flight
- **WHEN** parent P is in-progress via composite advancement, child A is running, and a caller runs `mo issue start B` for a Backlog child B
- **THEN** B SHALL start via the normal single-issue start path and SHALL NOT conflict with the parent's re-evaluation logic

### Requirement: Epic auto-advance SHALL drive a linked parent through composite advancement unchanged

A parent issue that is linked to an Epic SHALL be treated by the Epic as a normal issue. The Epic's auto-advance SHALL call the parent's start path, which SHALL fire composite advancement on the parent's children. The parent's aggregated Done state SHALL count toward Epic progress as a normal issue's Done state would. Composite advancement SHALL NOT require any change to Epic auto-advance, Epic link/unlink, or Epic done/close behavior.

#### Scenario: An Epic advances a parent like a normal issue and triggers composite advancement
- **WHEN** parent P is linked to a running Epic, the Epic's serial in-progress slot is free, and P has startable children
- **THEN** the Epic SHALL call the parent's start, composite advancement SHALL fire on P's children, and the Epic SHALL observe P as in-progress

#### Scenario: A parent's aggregated Done counts toward Epic progress
- **WHEN** parent P is linked to an Epic, all of P's children are terminal with at least one Done, and P's status is recomputed to Done
- **THEN** the Epic SHALL treat P as Done for the purpose of its readiness checks and auto-mark-done, identical to how it would treat a normal issue's Done state
