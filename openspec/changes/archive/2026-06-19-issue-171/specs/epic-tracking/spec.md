## MODIFIED Requirements

### Requirement: Projected Epic Progress

System SHALL project Epic progress from linked issue state at read time rather than storing progress as user-edited data. Progress SHALL be computed by a pure function over the linked issue read models and SHALL NOT mutate issue state.

The active and blocked issue sets SHALL be derived from each linked issue's `Health` field, not from its execution `Status`. An issue whose `Health` is `blocked` SHALL be counted as blocked, and an issue whose `Health` is `active` SHALL be counted as active. The sets SHALL NOT be empty merely because no issue's execution `Status` equals the strings `active` or `blocked`. `deliveredCount` and `totalIssueCount` SHALL continue to be derived from delivered and total linked issue counts.

`nextIssue` SHALL be the highest-priority linked issue that is currently startable — an issue whose derived start readiness reports `CanStart` with no undelivered `Blocker` — chosen by issue priority ascending (P0 before P4). `nextIssue` SHALL NOT be populated from an issue that is not startable, even if that issue was inserted first. When no linked issue is startable but undelivered work remains, the progress response SHALL convey a human-readable reason rooted in the blocking issue (for example, the undelivered prerequisite) instead of returning a non-startable issue. When every linked issue is delivered, the response SHALL indicate the Epic is ready to mark done.

Each entry in the active and blocked issue sets SHALL carry the issue's identity and presentation fields `{id, number, title, health}` so consumers can render concrete issues rather than opaque identifiers.

`readyToMarkDone` SHALL remain true if and only if the Epic has at least one linked issue and every linked issue is delivered. The mark-done judgment (`IsReadyToMarkDone`) SHALL depend solely on delivered/total counts and SHALL be unaffected by the `nextIssue` startability change.

#### Scenario: Delivered and total counts

- **WHEN** an Epic is listed or shown
- **THEN** `totalIssueCount` equals the number of linked issues
- **AND** `deliveredCount` equals the number of linked issues whose current state represents delivered work

#### Scenario: Active and blocked sets are derived from Health

- **WHEN** an Epic has linked issues whose execution `Status` is `in_progress` but whose runtime `Health` is `active` or `blocked`
- **THEN** the blocked set SHALL contain the issues whose `Health` is `blocked`
- **AND** the active set SHALL contain the issues whose `Health` is `active`
- **AND** the sets SHALL NOT be empty merely because no issue's `Status` equals the strings `active` or `blocked`

#### Scenario: Next issue is the highest-priority startable issue

- **WHEN** an Epic has multiple undelivered linked issues
- **AND** some of those issues are startable (`CanStart`, no undelivered `Blocker`)
- **THEN** `nextIssue` SHALL be the startable issue with the highest priority (P0 before P4)
- **AND** `nextIssue` SHALL NOT be a non-startable issue even if that issue was inserted first

#### Scenario: Next issue conveys a reason when nothing is startable

- **WHEN** an Epic has undelivered linked issues
- **AND** none of them is startable
- **THEN** `nextIssue` SHALL NOT be populated with a non-startable issue
- **AND** the progress response SHALL convey a human-readable reason identifying what blocks progress

#### Scenario: Ready to mark done depends only on delivered counts

- **WHEN** every linked issue of an Epic is delivered
- **THEN** `readyToMarkDone` SHALL be true
- **AND** the mark-done judgment SHALL remain correct regardless of the `nextIssue` startability change

#### Scenario: Active and blocked entries carry issue identity and health

- **WHEN** the active or blocked set is non-empty
- **THEN** each entry SHALL carry `{id, number, title, health}`
- **AND** a consumer SHALL be able to render the concrete issue without a further lookup

#### Scenario: Empty Epic progress

- **WHEN** an Epic has no linked issues
- **THEN** progress reports zero delivered and zero total
- **AND** the active and blocked sets are empty
- **AND** no issue workflow data is created or changed

## ADDED Requirements

### Requirement: Epic List Ordering

The Epic list read model SHALL return Epics grouped by status (`active`, `done`, `closed`) and, within each group, ordered by Epic priority ascending (P0 before P4) as the primary key and `updatedAt` descending as the secondary key. Ordering SHALL be performed by the server read model so that list consumers render Epics in the supplied order without re-sorting.

#### Scenario: Priority orders Epics within a status group

- **WHEN** two Epics share the same status
- **AND** one Epic has priority `p0` and the other has priority `p2`
- **THEN** the `p0` Epic SHALL precede the `p2` Epic in the list

#### Scenario: UpdatedAt breaks priority ties

- **WHEN** two Epics share the same status and the same priority
- **THEN** the Epic with the more recent `updatedAt` SHALL precede the other

#### Scenario: Consumers render in server-supplied order

- **WHEN** a list consumer renders Epics within a status group
- **THEN** it SHALL render them in the order supplied by the read model
- **AND** it SHALL NOT apply its own re-sort by priority or creation time
