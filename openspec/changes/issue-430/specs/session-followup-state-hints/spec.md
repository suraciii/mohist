### Requirement: Followup composer renders three explicit states that match real behavior

The `SessionFollowupComposer` SHALL render exactly three states, and the rendered state MUST correspond to the real user-facing behavior of the composer:

- **interactive** — the user can type and submit a followup message; the composer shows a clear "send a followup" placeholder and an enabled submit button.
- **queued** — a previous submit is awaiting the agent's first response; the input is disabled, and the composer shows a "queued" indicator that persists until the agent produces its first new part (or until the underlying followup mutation is no longer pending).
- **closed** — the session is no longer accepting followups (for example because it has ended); the input is disabled and the composer shows a closed-state message that mentions when the session ended.

The composer MUST NOT render the interactive state when the underlying send is unavailable (closed or queued), and MUST NOT render the closed state when the underlying send is available.

#### Scenario: Interactive state matches available send
- **WHEN** the composer is rendered with an enabled send path (for example `canFollowup` is true and no message is in flight)
- **THEN** the composer SHALL render the interactive state: enabled input, a clear "send a followup" placeholder, and an enabled submit button
- **AND** a submit SHALL actually call the provided `onSend`

#### Scenario: Closed state matches unavailable send
- **WHEN** the composer is rendered with the session no longer accepting followups (for example `canFollowup` is false because the session has ended)
- **THEN** the composer SHALL render the closed state: the input SHALL be disabled
- **AND** the rendered message SHALL indicate the session is closed
- **AND** the placeholder and submit affordance SHALL NOT be presented as if the user could send a followup

#### Scenario: Queued state matches in-flight submit
- **WHEN** the composer has a submitted followup still awaiting the agent's first new part (for example the followup mutation is pending, or a queued flag is set upstream)
- **THEN** the composer SHALL render the queued state: the input SHALL be disabled
- **AND** a "queued" indicator SHALL be visible and SHALL persist for the duration of the queue, not only as a transient flash

### Requirement: Interactive state shows a clear placeholder and submit affordance

In the interactive state, the composer SHALL show a placeholder that names the action (for example containing the phrase "Send a followup" or equivalent) and SHALL expose an enabled submit control that, when activated with non-empty trimmed text, SHALL call the supplied `onSend` handler. The submit control SHALL be disabled while the trimmed input is empty.

#### Scenario: Empty input keeps submit disabled in interactive state
- **WHEN** the composer is in the interactive state with an empty or whitespace-only input
- **THEN** the submit control SHALL be disabled

#### Scenario: Non-empty input enables submit and triggers onSend
- **WHEN** the composer is in the interactive state and the user enters non-whitespace text
- **THEN** the submit control SHALL become enabled
- **AND** activating submit (button click or Enter key without Shift) SHALL invoke the supplied `onSend` handler with the trimmed text

### Requirement: Closed state names when the session ended

In the closed state, the composer SHALL render a message that includes a reference to when the session ended (for example "Session ended <relative time> — not accepting new followups."). The reference MUST be derived from the session's `completedAt` (or the equivalent end timestamp) rather than the wall clock directly, and the helper that produces the relative phrase MUST be the same helper used elsewhere on the page so that the phrasing is consistent. When no end timestamp is available, the composer SHALL fall back to a generic "Session ended" message rather than fabricating a time.

#### Scenario: Closed state with completedAt renders a relative-time message
- **WHEN** the composer is in the closed state and the session has a `completedAt` value
- **THEN** the composer SHALL render a message that includes a relative-time reference derived from `completedAt` (for example "Session ended 8h ago — not accepting new followups.")

#### Scenario: Closed state without completedAt renders a generic message
- **WHEN** the composer is in the closed state and the session has no `completedAt` value
- **THEN** the composer SHALL render a generic "Session ended" message
- **AND** SHALL NOT fabricate or guess a time value

### Requirement: Queued state keeps the indicator visible until the queue clears

In the queued state, the composer SHALL disable the input and SHALL show a queued indicator that remains visible as long as the queue is active. The indicator SHALL disappear (transitioning back to interactive) once the queue clears — specifically, when the underlying followup mutation is no longer pending AND the upstream queue flag is unset AND the agent has produced its first new part after the submit. The queued state MUST NOT collapse into the existing transient `Sent` flash (which is a momentary confirmation) and SHALL persist until those conditions are met.

#### Scenario: Queued indicator persists while the mutation is pending
- **WHEN** the composer transitions into the queued state because a submit was sent
- **THEN** the input SHALL be disabled
- **AND** a queued indicator SHALL remain visible as long as the underlying followup mutation is pending

#### Scenario: Queued state clears when the agent responds
- **WHEN** the composer is in the queued state and the agent produces its first new part after the submit
- **THEN** the queued indicator SHALL disappear
- **AND** the composer SHALL return to the interactive state (input enabled, placeholder visible)

### Requirement: Followup composer props are backward compatible

The composer SHALL accept the existing `onSend`, `isSending`, `disabled`, `placeholder`, and `className` props with unchanged semantics. New props MAY be added to feed the queued and closed states (for example `endedAt`, `statusKind`, `hasQueuedFollowup`); when those new props are omitted, the composer SHALL render with the same observable behavior as before for interactive and closed states.

#### Scenario: Existing props still drive the disabled and submit behavior
- **WHEN** the composer is rendered with only the existing props (`onSend`, `isSending`, `disabled`, `placeholder`, `className`) and no new props
- **THEN** the disabled-when-`disabled` behavior and the submit-when-not-disabled behavior SHALL be unchanged
- **AND** the closed-state message SHALL fall back to the prior generic phrasing when `endedAt` is not provided

### Requirement: Followup composer state changes are presentational only

The followup composer state changes SHALL be purely presentational. They SHALL NOT alter the `onSend` mutation, the `sendFollowup` function, the `canFollowup` derivation, the liveness gate, or any data source field. The same inputs SHALL produce the same send behavior before and after the change; only the rendered state, copy, and indicator change.

#### Scenario: Send path and data source are unchanged
- **WHEN** the composer is rendered with the same `onSend`, `isSending`, and `disabled` inputs
- **THEN** the actual call to `onSend` (URL, body, idempotency, error handling) SHALL be unchanged
- **AND** no new field SHALL be added to the session data source to support the queued state beyond an optional `hasQueuedFollowup` (or equivalent) signal already supplied by the caller
