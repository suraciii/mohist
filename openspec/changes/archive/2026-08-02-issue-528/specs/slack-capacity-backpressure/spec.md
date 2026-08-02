### Requirement: Backpressure is reversible without operator intervention

When a Connection enters Degraded (Backpressured) because its provider inbox or outbound outbox reached capacity, Mohist SHALL clear the backpressure and return the Connection to Healthy as soon as the backlog has drained below capacity, without requiring the operator to disable, reconfigure, or recreate the Connection. A backpressure event SHALL NOT be a terminal state; the recovery SHALL be driven by the same periodic sweep that advances the outbox, comparing current pending inbox and pending outbox counts against their per-connection capacity thresholds.

#### Scenario: Outbox drains and the Connection recovers
- **WHEN** a Connection is Degraded (Backpressured) on outbox overflow and pending outbox entries are delivered until the pending count falls below the outbox capacity threshold
- **THEN** the Connection's health is set back to Healthy, the Backpressured health reason is cleared, and new Slack input is accepted again without any operator action

#### Scenario: Inbox drains and the Connection recovers
- **WHEN** a Connection is Degraded (Backpressured) on inbox overflow and pending inbox entries are dispatched until the pending count falls below the inbox capacity threshold
- **THEN** the Connection's health is set back to Healthy, the Backpressured health reason is cleared, and new Slack input is accepted again without any operator action

#### Scenario: Backpressure persists while either side remains full
- **WHEN** a Connection is Degraded (Backpressured) and the inbox has drained but the outbox is still at capacity
- **THEN** the Connection remains Degraded (Backpressured) and continues to reject new Slack input until both sides are below their thresholds

### Requirement: Recovery never drops accepted inputs or terminal deliveries

Flipping a Connection out of backpressure SHALL NOT delete or alter any already-accepted inbox event, SessionInput, or outbox delivery intent. Accepted inputs remain accepted and terminal results, explicit failures, and user-action messages remain pending until delivered, dead-lettered, or resolved by an operator; the recovery transition only reopens ingress.

#### Scenario: Accepted inputs survive recovery
- **WHEN** a Connection recovers from backpressure
- **THEN** every inbox event and SessionInput that was accepted before and during the backpressure episode is still present and unchanged

#### Scenario: Terminal deliveries remain pending through recovery
- **WHEN** a Connection recovers from outbox overflow while terminal-result or explicit-failure outbox rows are still unsent
- **THEN** those rows remain pending in the outbox and are still delivered afterward; none are silently dropped by the recovery

### Requirement: Backpressured is a distinct, honestly surfaced diagnostic state

The Connection diagnostic SHALL surface Backpressured as its own most-important state, distinct from Disabled (an operator choice), credentials invalid, service offline, and a healthy operating state. A backpressured Connection SHALL NOT be reported as Healthy. The diagnostic SHALL name whether the pressure is on the inbox or the outbox and SHALL give a single actionable next step (wait for the backlog to drain, or retry input shortly).

#### Scenario: A backpressured Connection is not reported healthy
- **WHEN** a Connection is Degraded (Backpressured) and an operator opens it in the Web UI or runs the CLI view
- **THEN** the diagnostic surfaces Backpressured as the most-important state with the inbox-or-outbox reason and a single wait/retry next action, rather than reporting the Connection as healthy

#### Scenario: Backpressured is distinct from Disabled
- **WHEN** one Connection is backpressured and another is Disabled by operator choice
- **THEN** the two produce distinguishable diagnostics — Backpressured reports an external capacity condition with a wait/retry action, while Disabled reports an operator choice with an enable action

### Requirement: Backpressure ingress rejection is visible to the sender

When ingress is refused because the Connection is backpressured, the refusal SHALL be communicated as a presentable, structured rejection that the Slack sender can observe — not as a silent transport-level error. The sender SHALL be able to tell that their input was not accepted and that they should retry shortly, which is distinct from an input that was accepted but whose result has not yet been delivered.

#### Scenario: Sender sees a not-accepted rejection under backpressure
- **WHEN** a Slack member sends a message to a backpressured Connection
- **THEN** the refusal is surfaced to the sender as a not-accepted result with a retry-later reason, rather than the message silently disappearing

#### Scenario: Not-accepted is distinguishable from accepted-but-pending
- **WHEN** one message is refused under backpressure and another was accepted just before the backpressure episode
- **THEN** the sender can distinguish the refused message (not accepted, retry) from the accepted one (result still pending)
