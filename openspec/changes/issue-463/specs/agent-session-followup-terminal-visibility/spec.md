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

### Requirement: Web converges session state on a follow-up terminal event

On receiving a follow-up terminal event for a session, the web SHALL update that session's presented state so it converges to the corresponding terminal state.

#### Scenario: Completed follow-up converges to completed state

- **WHEN** the web receives `session.followup_completed` for a session
- **THEN** the session's presented state SHALL become completed

#### Scenario: Failed follow-up converges to failed state

- **WHEN** the web receives `session.followup_failed` for a session
- **THEN** the session's presented state SHALL become failed
