### Requirement: Page-level cancel affordance on the generic session detail page

The generic (agent-launch) agent session detail page SHALL surface a page-level cancel/stop control so a user can stop a session they started. The control SHALL live in the page header region and SHALL NOT be placed inside the followup composer, preserving the issue-242 composer constraint that the composer includes no stop control.

The affordance is exclusive to the generic session page: only the generic session data source SHALL expose the cancel mutation (and the running flag) through the shared session data result; the issue/workflow session data source SHALL NOT expose it.

#### Scenario: Running generic session shows a page-level cancel control

- **WHEN** a user views a generic (agent-launch) session detail page for a session that is currently running/active
- **THEN** the page header region SHALL render a cancel/stop control
- **AND** the cancel control SHALL NOT be rendered inside the followup composer

#### Scenario: Followup composer stays free of any stop control

- **WHEN** the followup composer is rendered on the generic session page
- **THEN** the composer SHALL NOT include a stop control, steering control, or stage-control dashboard
- **AND** the only cancel affordance SHALL be the page-level control in the header

### Requirement: Cancel control visibility is tied to non-terminal state

The cancel control SHALL be presented only while the generic session is in a running/active (non-terminal) state. Once the session reaches a terminal state (`completed`, `failed`, `cancelled`, or `stopped`), the control SHALL be hidden or disabled so it cannot be invoked.

#### Scenario: Non-terminal session shows an interactive cancel control

- **WHEN** the generic session status is `active`, `running`, or `probing` (non-terminal)
- **THEN** the cancel control SHALL be visible and interactive

#### Scenario: Terminal session hides or disables the cancel control

- **WHEN** the generic session status is `completed`, `failed`, `cancelled`, or `stopped`
- **THEN** the cancel control SHALL be hidden or disabled
- **AND** the user SHALL NOT be able to invoke cancel from the page

### Requirement: Confirmation gate prevents accidental cancellation

Invoking cancel SHALL require an explicit confirmation step using a destructive-toned confirmation dialog, so accidental clicks do not fire the cancel request. No cancel request SHALL be sent until the user explicitly confirms.

#### Scenario: Activating cancel opens a destructive confirmation dialog

- **WHEN** the user activates the cancel control
- **THEN** a confirmation dialog in a destructive tone SHALL open
- **AND** no cancel request SHALL be sent before the user confirms

#### Scenario: Dismissing the dialog cancels no session

- **WHEN** the user dismisses the confirmation dialog without confirming
- **THEN** no cancel request SHALL be sent
- **AND** the session SHALL continue running unchanged

#### Scenario: Confirming invokes the cancel mutation

- **WHEN** the user confirms in the destructive confirmation dialog
- **THEN** the page SHALL invoke the generic-session cancel mutation

### Requirement: Cancel is best-effort and refreshes session state on success

Confirming cancel SHALL invoke the pre-existing generic-session cancel mutation (a `POST` to the `.../agent-sessions/{sessionId}/cancel` endpoint). The cancel is best-effort: the backend fires an ACP `session/cancel` notification and reports the honest observed state; the page SHALL NOT guarantee that the agent honours the cancel or that the session reaches a terminal state as a result. On a successful response, the page SHALL invalidate the session summary and transcript queries so the page reflects the resulting state without a manual refresh.

#### Scenario: Confirming triggers the existing cancel endpoint

- **WHEN** the user confirms the cancel dialog
- **THEN** the page SHALL call the generic-session cancel mutation against `POST .../agent-sessions/{sessionId}/cancel`
- **AND** SHALL NOT introduce any new backend cancel capability

#### Scenario: Successful cancel refreshes session state without a manual reload

- **WHEN** the cancel mutation succeeds
- **THEN** the session summary and transcript queries SHALL be invalidated
- **AND** the page SHALL reflect the resulting session state without requiring a manual refresh

#### Scenario: Non-terminal outcome is surfaced honestly

- **WHEN** the backend reports a non-terminal outcome (e.g. `not-cancellable`) because no live ACP session exists or the agent did not honour the cancel
- **THEN** the page SHALL surface the honest reported state
- **AND** SHALL NOT fabricate a `cancelled` outcome

### Requirement: Issue/workflow session pages remain free of any stop control

The issue and workflow session detail pages (the `SessionPage` route) SHALL NOT render any cancel/stop control anywhere — neither in the header nor in the composer. Only the generic (agent-launch) session page exposes the cancel affordance. A second layer of guard backs this boundary: the runner's cancel handler SHALL reject any non-`generic` target with a `not-cancellable` state, so a misrouted cancel request cannot stop an issue/workflow session.

#### Scenario: Issue/workflow session page renders no cancel control

- **WHEN** a user views an issue or workflow session detail page (the `SessionPage` route)
- **THEN** the page SHALL NOT render a cancel/stop control in the header or the composer
- **AND** the page SHALL remain unchanged from its pre-issue-349 behaviour

#### Scenario: Non-generic cancel target is rejected by the runner

- **WHEN** a cancel request is directed at a non-`generic` (issue/workflow) session target
- **THEN** the runner SHALL reject it with a `not-cancellable` state
- **AND** the targeted session SHALL NOT be cancelled
