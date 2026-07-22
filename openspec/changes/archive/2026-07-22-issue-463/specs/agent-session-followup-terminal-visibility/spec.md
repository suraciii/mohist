### Requirement: Web subscribes to follow-up terminal events

The web's canonical transcript event subscription set SHALL include both follow-up terminal event types — `session.followup_completed` and `session.followup_failed` — so these events pass the server's per-connection delivery filter instead of being dropped as having no subscribers.

#### Scenario: Follow-up terminal types are in the subscription set

- **WHEN** the web establishes or refreshes its transcript event subscription with the server
- **THEN** the subscription set SHALL include `session.followup_completed` and `session.followup_failed`

#### Scenario: Subscription routing guard stays satisfied

- **WHEN** the follow-up terminal types are added to the subscription set and routed to handlers
- **THEN** the web's compile-time subscription/routing guards SHALL continue to hold (every routed event type remains a subscribed type)

### Requirement: Follow-up terminal events are delivered to the web

The server SHALL deliver `session.followup_completed` and `session.followup_failed` events to web clients that subscribe to them. The web SHALL accept these events rather than ignoring them as unknown transcript event types.

#### Scenario: Completed follow-up reaches the web

- **WHEN** a follow-up completes and the runner emits `session.followup_completed`
- **THEN** the web SHALL receive that event

#### Scenario: Failed follow-up reaches the web

- **WHEN** a follow-up fails and the runner emits `session.followup_failed`
- **THEN** the web SHALL receive that event

### Requirement: Web converges the in-flight follow-up round and refreshes session status on a terminal event

A follow-up terminal event is operation-scoped: it ends the in-flight follow-up, not the session (the session does not become globally completed or failed, and remains usable for further follow-ups). On receiving a follow-up terminal event for a session, the web SHALL converge the in-flight follow-up round to the corresponding outcome (completed or failed) and SHALL refresh the session's presented status from the server. The web SHALL NOT mark the session itself as globally completed or failed solely from a follow-up terminal event.

#### Scenario: A completed follow-up closes the in-flight round

- **WHEN** the web receives `session.followup_completed` for a session
- **THEN** the in-flight follow-up round SHALL converge to completed, and the session's presented status SHALL be refreshed from the server rather than set to a global completed terminal

#### Scenario: A failed follow-up marks the in-flight round failed

- **WHEN** the web receives `session.followup_failed` for a session
- **THEN** the in-flight follow-up round SHALL converge to failed, and the session's presented status SHALL be refreshed from the server rather than set to a global failed terminal
