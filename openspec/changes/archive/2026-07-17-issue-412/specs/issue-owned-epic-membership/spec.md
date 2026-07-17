### Requirement: Issue is the sole authority for current Epic affiliation

Issue SHALL store nullable `EpicNumber` as its current affiliation. Assigning a different Epic SHALL
replace the old number in one Issue transaction and raise `IssueEpicChanged(previous, current)`.
Epic SHALL NOT own a writable membership row, active-membership row, or independently mutable member
collection.

#### Scenario: An unaffiliated Issue joins an Epic

- **WHEN** Issue 42 accepts assignment to Epic 7
- **THEN** Issue 42 commits `EpicNumber = 7` and its own affiliation event atomically
- **AND** no Epic state or membership table is written in that transaction

#### Scenario: An Issue moves directly between Epics

- **GIVEN** Issue 42 currently has `EpicNumber = 7`
- **WHEN** it accepts assignment to Epic 9
- **THEN** one Issue commit changes the value from 7 to 9
- **AND** no state exists in which both Epics own the Issue

### Requirement: Link and unlink commands are idempotent and guarded

`Epic.LinkIssue` SHALL return success when the Issue already belongs to that Epic. A closed Epic
SHALL reject a genuinely new assignment. Removing affiliation SHALL include the expected Epic
number so a delayed old command cannot clear a newer affiliation.

#### Scenario: Link reply is lost and Epic later closes

- **GIVEN** the Issue assignment committed but the caller did not receive the response
- **AND** the Epic subsequently became closed
- **WHEN** the same LinkIssue command is retried
- **THEN** it observes the Issue already belongs to that Epic and returns idempotent success

#### Scenario: Old Epic unlink arrives after a move

- **GIVEN** Issue 42 moved from Epic 7 to Epic 9
- **WHEN** `RemoveEpic(expectedEpicNumber: 7)` arrives late
- **THEN** Issue keeps `EpicNumber = 9`
- **AND** no affiliation event is raised

### Requirement: Epic derives membership and progress from Issue state

Epic member lists, progress counts, completion checks, and startable candidates SHALL be queries over
current Issue state. Epic SHALL own only its lifecycle and progression policy. Candidate query
staleness SHALL NOT violate Issue invariants because the target Issue revalidates the command.

#### Scenario: A stale candidate is no longer in the Epic

- **GIVEN** Epic 7 queried Issue 42 as startable
- **AND** Issue 42 moved to Epic 9 before the command arrived
- **WHEN** Epic 7 sends `TryStartFromEpic(7)`
- **THEN** Issue 42 rejects or no-ops without starting work

#### Scenario: Open Issue is assigned to a done Epic

- **WHEN** an open Issue commits affiliation to a done Epic
- **THEN** the durable affiliation reaction causes that Epic to recompute and converge to running
- **AND** the Epic state change occurs in a separate Epic transaction

### Requirement: Affiliation reactions resolve current truth

The durable reaction to `IssueEpicChanged` SHALL notify affected old/new Epics and refresh the active
WorkflowRun, but SHALL re-read the Issue's current state before constructing target commands. Event
payload order SHALL NOT become a second affiliation authority.

#### Scenario: Affiliation events are delivered out of order

- **GIVEN** Issue moved from Epic 7 to 8 and then to 9
- **WHEN** the first change event is delivered after the second
- **THEN** handlers read current `EpicNumber = 9`
- **AND** no Epic or WorkflowRun is reverted to 8
