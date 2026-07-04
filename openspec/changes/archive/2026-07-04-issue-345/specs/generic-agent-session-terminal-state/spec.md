### Requirement: Generic session reaches a terminal state on job completion, including the success path

A generic (agent-launch) AgentSession SHALL reach a terminal state (`completed` or `failed`) when its standalone agent job finishes, on BOTH the success and failure paths. When a generic agent job completes successfully, the system SHALL record a `session.closed` transcript event carrying a terminal `status` of `completed`, so the server's terminal-state derivation — which keys off a persisted `session.closed` transcript part — can resolve the session out of `running`.

The success path SHALL NOT be suppressed for a generic agent job. A session whose job has finished successfully MUST NOT remain indefinitely in `running` because no `session.closed` event was persisted.

#### Scenario: Successful job drives the session to completed

- **WHEN** a generic agent job finishes successfully (the agent task completed and expectations were satisfied)
- **THEN** the system SHALL record a `session.closed` event for that session with `status` of `completed`
- **AND** the session's resolved status (session detail page and list/summary) SHALL become `completed`
- **AND** the session SHALL NOT remain in `running` after the job has finished

#### Scenario: Failed job drives the session to failed

- **WHEN** a generic agent job finishes in failure (the agent task failed, timed out, or expectations were not satisfied)
- **THEN** the system SHALL record a `session.closed` event for that session with `status` of `failed`
- **AND** the session's resolved status SHALL become `failed`

#### Scenario: Terminal-state derivation resolves from the persisted close event

- **WHEN** the server resolves a generic session's status and a `session.closed` transcript part exists for it
- **THEN** the server SHALL derive the terminal status from that persisted close event
- **AND** SHALL NOT report `running` for a session whose job has already recorded a terminal close

#### Scenario: Session no longer hangs in running after a successful run

- **WHEN** a generic session's agent job has completed successfully and some time passes
- **THEN** the session status observed via the session detail page and list SHALL be `completed`
- **AND** SHALL NOT stay at `running` indefinitely regardless of how long the caller waits

### Requirement: Job-completion is decoupled from the runner's cached ACP-session lifetime

The signal that drives a generic session to a terminal state SHALL be the agent job's completion, decoupled from the runner's cached ACP-session lifetime. The runner MAY retain its ACP session for follow-up messages without that retention suppressing the server-side terminal-state transition. Caching the ACP session for reuse SHALL NOT be a reason to withhold the job-completion `session.closed` event.

#### Scenario: Cached ACP session does not block the success-path close

- **WHEN** a generic agent job succeeds and the runner keeps its ACP session cached so it can serve a later follow-up
- **THEN** the system SHALL still record the job-completion `session.closed` event for the session
- **AND** the server-side session SHALL transition to `completed`
- **AND** the retained ACP session SHALL remain usable for a subsequent follow-up that re-opens/derives activity on the session

#### Scenario: Follow-up after a completed session observes the prior terminal state

- **WHEN** a follow-up is delivered to a generic session whose prior job completed
- **THEN** the server SHALL observe the recorded terminal state (`completed`) for that session
- **AND** SHALL NOT report the session as perpetually `running` due to the runner's retained ACP session
