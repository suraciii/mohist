# OpenSpec Capability: epic-tracking

### Requirement: Epic Domain Model

System SHALL model an Epic as a named, described, prioritized long-running goal container with `active`, `paused`, `done`, and `closed` statuses. The `paused` status SHALL be a reversible, non-terminal lifecycle state that is distinct from any issue-health "blocked" concept; the system SHALL NOT reuse the word "blocked" for an Epic state.

#### Scenario: Create active Epic

- **WHEN** a user creates an Epic with title, description, and priority
- **THEN** the system persists the Epic with status `active`
- **AND** the Epic has timestamps suitable for list and detail display

#### Scenario: Epic is not executable work

- **WHEN** the system stores or reads Epics
- **THEN** Epics are separate from issues
- **AND** Epics do not have workflow stage, run state, worktree, branch, task execution, or check execution fields

#### Scenario: Paused is a reversible non-terminal state

- **WHEN** an Epic transitions into `paused`
- **THEN** the Epic is not considered terminal (`IsTerminal` SHALL remain true only for `done` and `closed`)
- **AND** the Epic MUST be able to return to `active` via Resume
- **AND** the Epic status enum and derived projection logic SHALL treat `paused` as a distinct branch alongside `active`, `done`, and `closed`

### Requirement: Primary Epic Issue Membership

System SHALL allow an Epic to link existing issues while enforcing that an issue belongs to at most one primary Epic in the first version.

#### Scenario: Add issue to Epic

- **WHEN** a user adds an existing issue to an existing Epic
- **THEN** the issue appears in the Epic linked issue list
- **AND** the issue workflow state is unchanged

#### Scenario: Remove issue from Epic

- **WHEN** a user removes a linked issue from an Epic
- **THEN** only the Epic membership is removed
- **AND** the issue workflow state, prerequisite data, and worktree data are unchanged

#### Scenario: Reject duplicate primary membership

- **WHEN** a user adds an issue that already belongs to another Epic
- **THEN** the system rejects the add operation with a clear error identifying the existing Epic
- **AND** the existing membership is preserved

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

### Requirement: Epic Lifecycle

System SHALL let users explicitly pause, resume, mark done, or close an Epic without automatically completing it from issue progress. Status transitions SHALL obey the following locked rules: `active ↔ paused` via `Pause()` and `Resume()` SHALL be legal; `paused → closed` via `Close()` SHALL be legal; `paused → done` SHALL be forbidden. The `done` and `closed` statuses are terminal; `paused` and `active` are non-terminal. Entering `paused` SHALL NOT unbind any linked issue, SHALL NOT change issue workflow state, and SHALL NOT block Edit, link, unlink, or Close operations on the Epic. A terminal-state guard SHALL reject transitions out of `done` or `closed` but SHALL NOT fire on `paused`.

#### Scenario: Pause an active Epic

- **WHEN** a user pauses an `active` Epic, optionally supplying a pause reason
- **THEN** only the Epic status changes to `paused`
- **AND** the optional pause reason is persisted on the Epic
- **AND** linked issues are not modified or unbound
- **AND** the Epic is not considered terminal

#### Scenario: Resume a paused Epic

- **WHEN** a user resumes a `paused` Epic
- **THEN** only the Epic status changes back to `active`
- **AND** the persisted pause reason is cleared
- **AND** linked issues are not modified

#### Scenario: Close a paused Epic directly

- **WHEN** a user closes a `paused` Epic
- **THEN** the transition is allowed without first resuming
- **AND** only the Epic status changes to `closed`

#### Scenario: Paused Epic cannot be marked done directly

- **WHEN** a user attempts to mark done on a `paused` Epic
- **THEN** the system rejects the transition
- **AND** the error indicates the Epic MUST be resumed to `active` first

#### Scenario: Mark Epic done

- **WHEN** a user marks an `active` Epic done
- **THEN** only the Epic status changes to `done`
- **AND** linked issues are not modified

#### Scenario: Close Epic

- **WHEN** a user closes an `active` or `paused` Epic
- **THEN** only the Epic status changes to `closed`
- **AND** linked issues are not modified

#### Scenario: No automatic completion

- **WHEN** all linked issues are delivered
- **THEN** the system does not automatically mark the Epic done
- **AND** the projected next state can indicate ready to mark done

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

#### Scenario: Paused Epic remains editable

- **WHEN** an Epic is `paused`
- **THEN** a user can still Edit the Epic, link issues, unlink issues, and Close the Epic
- **AND** the terminal-state guard does not reject these operations on `paused`

### Requirement: Linked Issue Read Model Carries Prerequisite Edges

The Epic linked-issue read model (`LinkedIssueDto` returned by `EpicQuerier.GetLinkedIssuesAsync`, or an equivalent Epic-scoped projection) SHALL carry, for each linked issue, its `prerequisiteNumbers` so that a client can render the dependency graph without issuing a per-issue fetch. For each prerequisite that is not a member of the Epic (an external prerequisite), the read model SHALL include enough summary — at minimum the issue number, title, and status/delivery state — to render it as a distinct external node. The prerequisite-edge data on the read model SHALL be additive and SHALL NOT alter the existing `Projected Epic Progress` semantics: `deliveredCount`, `totalIssueCount`, the active and blocked sets, `nextIssue` selection, and `readyToMarkDone` SHALL continue to be derived exactly as before.

#### Scenario: Prerequisite numbers are available per linked issue

- **WHEN** the Epic linked-issue read model is returned for an Epic
- **THEN** each linked issue entry SHALL carry its `prerequisiteNumbers`
- **AND** a client SHALL be able to render prerequisite edges without an additional per-issue lookup

#### Scenario: External prerequisite summary is available

- **WHEN** a linked issue has a prerequisite that is not a member of the same Epic
- **THEN** the read model SHALL include a summary of that external prerequisite carrying at least its number, title, and status/delivery state
- **AND** the summary SHALL be sufficient to render the external prerequisite as a distinct node

#### Scenario: Prerequisite edges do not change progress semantics

- **WHEN** prerequisite-edge data is added to the linked-issue read model
- **THEN** `deliveredCount`, `totalIssueCount`, the active and blocked sets, `nextIssue`, and `readyToMarkDone` SHALL continue to be derived exactly as in `Projected Epic Progress`
- **AND** the additive field SHALL NOT change any existing progress, next-issue, or mark-done outcome
