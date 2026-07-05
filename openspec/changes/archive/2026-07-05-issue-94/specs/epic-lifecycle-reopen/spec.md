### Requirement: Reopen recovers an epic from a terminal state to idle

A `Reopen` transition SHALL recover an epic from a terminal state (`done` or `closed`) back to `idle`, symmetric to the issue domain's `Reopen` (`cancelled` → `backlog`). Reopen SHALL be the only way out of a terminal state: the existing `EnsureNotTerminal` guard that blocks Start, Pause, Resume, Mark Done, and Close from terminal states SHALL continue to hold. Reopen SHALL be exposed as a domain `Reopen()` method on the `Epic` aggregate, a grain `ReopenAsync()` entry point, an HTTP `POST /{id}/reopen` route, and a web detail-page action.

#### Scenario: Reopen from done returns to idle

- **WHEN** `Reopen` is invoked on an epic in the `done` state
- **THEN** the epic SHALL transition to `idle`
- **AND** the epic SHALL no longer be terminal

#### Scenario: Reopen from closed returns to idle

- **WHEN** `Reopen` is invoked on an epic in the `closed` state
- **THEN** the epic SHALL transition to `idle`
- **AND** the epic SHALL no longer be terminal

#### Scenario: Reopen from a non-terminal state is rejected

- **WHEN** `Reopen` is invoked on an epic in `idle`, `running`, or `paused`
- **THEN** the request SHALL be rejected
- **AND** the epic SHALL remain in its current state

### Requirement: Reopen re-establishes active issue memberships released on terminalization

Terminalizing an epic (`done`/`closed`) releases its active issue memberships (`EpicActiveIssues`) while retaining its link records (`EpicIssues`). Reopen SHALL re-establish the active membership for each linked issue so the epic can resume driving those issues — but SHALL honor the same cross-epic active-membership uniqueness invariant as a single link: an issue already actively owned by another non-terminal (`idle`/`running`/`paused`) epic SHALL NOT be re-claimed. Issues that cannot be re-claimed (because they were re-homed to another non-terminal epic during the terminal period) SHALL remain linked to this epic without blocking the reopen or the re-claim of the remaining issues.

#### Scenario: Reopen re-claims linked issues not owned elsewhere

- **WHEN** an epic is reopened from a terminal state
- **AND** none of its linked issues are actively owned by another non-terminal epic
- **THEN** an active membership SHALL be re-established for every linked issue
- **AND** those issues SHALL project this epic as their primary epic

#### Scenario: Reopen skips issues re-homed to another non-terminal epic

- **WHEN** an epic is reopened from a terminal state
- **AND** one of its linked issues was re-homed (actively owned) by another non-terminal epic during the terminal period
- **THEN** reopen SHALL NOT re-claim that issue's active membership
- **AND** reopen SHALL still re-claim the active memberships of the remaining linked issues
- **AND** reopen SHALL NOT fail because of the re-homed issue

### Requirement: Reopen records an EpicReopened event

Reopen SHALL record a new `EpicReopened` domain event variant (added to the `EpicEvent` union), mirroring `IssueReopened`. The transition SHALL also record an `EpicStatusChanged` event (terminal-status → `idle`), consistent with the other status transitions. The dedicated `EpicReopened` variant SHALL let the activity timeline distinguish a recovery from generic status churn.

#### Scenario: Reopen emits a dedicated reopen event

- **WHEN** an epic is reopened from `done` or `closed`
- **THEN** the persisted event stream for that epic SHALL include an `EpicReopened` event
- **AND** SHALL include a status-changed event from the prior terminal status to `idle`

### Requirement: Reopen HTTP route

A `POST /api/projects/{projectRef}/epics/{id}/reopen` route SHALL invoke the reopen grain operation and return the updated epic. The `{id}` segment SHALL accept either the epic's internal id or its number, consistent with the other epic routes. A reopen attempted on a non-terminal epic SHALL produce a conflict response (HTTP 409) with a distinct terminal-guard error code. A reopen on a missing epic SHALL produce HTTP 404.

#### Scenario: POST reopen recovers a terminal epic

- **WHEN** a client sends `POST /epics/{id}/reopen` for a `done` or `closed` epic
- **THEN** the response SHALL be HTTP 200 with the updated epic in the `idle` state

#### Scenario: POST reopen on a non-terminal epic is rejected

- **WHEN** a client sends `POST /epics/{id}/reopen` for an `idle`, `running`, or `paused` epic
- **THEN** the response SHALL be HTTP 409 with a terminal-guard error code
- **AND** the epic's state SHALL be unchanged

#### Scenario: POST reopen on a missing epic is not found

- **WHEN** a client sends `POST /epics/{id}/reopen` for an epic id/number that does not exist
- **THEN** the response SHALL be HTTP 404

### Requirement: Web detail page offers reopen for terminal epics

The web detail page's primary lifecycle action SHALL return a `reopen` action for terminal (`done`/`closed`) epics (today it returns `null`), and the detail page SHALL render a "Reopen" control that calls the reopen endpoint. A successful reopen SHALL invalidate the epic queries so the page reflects the recovered `idle` state and the re-established linked issues. A non-terminal epic SHALL NOT show a reopen control.

#### Scenario: Terminal epic shows a Reopen action

- **WHEN** the detail page loads a `done` or `closed` epic
- **THEN** a "Reopen" control SHALL be visible
- **AND** invoking it SHALL call the reopen endpoint and transition the epic to `idle`

#### Scenario: Non-terminal epic does not show reopen

- **WHEN** the detail page loads a non-terminal (`idle`/`running`/`paused`) epic
- **THEN** no "Reopen" control SHALL be shown
