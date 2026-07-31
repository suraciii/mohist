### Requirement: Continue a Session with follow-up input
The Web SHALL submit a follow-up as a new SessionInput on the existing AgentSession and MUST NOT create a second Session or AgentJob. When the Session is idle, an accepted follow-up MUST begin a new AgentTurn; when a turn is in progress, the page MUST present the authoritative accepted, queued, executing, rejected, or unknown outcome. A follow-up with an unknown outcome MUST be retried only with its original idempotency key.

#### Scenario: Follow-up is accepted while the Session is idle
- **WHEN** a user submits a valid follow-up to an idle Session with an available runtime binding
- **THEN** the page reports acceptance, shows the new input and turn on the same Session, and does not create another AgentJob

#### Scenario: Follow-up outcome is unknown
- **WHEN** the server cannot confirm whether a submitted follow-up was accepted
- **THEN** the page displays the outcome as unknown and retains the original idempotency key for an explicit retry

### Requirement: Control the current turn safely
The Web SHALL offer cancellation only for a queued current turn and SHALL offer stop only for an executing current turn. Cancelling a queued turn MUST be presented as a terminal cancellation. Stopping an executing turn MUST be presented as a request until the authoritative result is confirmed; an unconfirmed stop MUST remain unknown rather than being presented as stopped.

#### Scenario: Queued turn is cancelled
- **WHEN** a user confirms cancellation of a queued current turn
- **THEN** the page reports the turn as cancelled and converges the Session view to the authoritative result

#### Scenario: Active turn stop is not confirmed
- **WHEN** a user requests a stop for an executing turn and the runtime outcome cannot be confirmed
- **THEN** the page reports an unknown stop outcome and does not label the turn as stopped or the Session as idle

### Requirement: Maintain Session context safely
The Web SHALL offer Compact and Reset only when the Session is safely idle. Compact MUST request native runtime compaction without changing the logical AgentSession or its current runtime binding. Reset MUST require explicit confirmation and MUST create a new empty runtime context while retaining the logical AgentSession, its transcript, and audit history; the page MUST identify that later activity begins from reset context.

#### Scenario: User resets an idle Session
- **WHEN** a user confirms Reset for an idle Session
- **THEN** the page retains the same Session history, reports that subsequent work uses a new empty runtime context, and does not create a new logical Session

#### Scenario: Session is active during context maintenance
- **WHEN** a Session has a queued or executing turn
- **THEN** Compact and Reset are unavailable and the page explains that the active turn must finish or be controlled first

### Requirement: Command outcomes converge to Session truth
After a follow-up, turn-control, Compact, or Reset command, the Web SHALL refresh or reconcile the Session summary and transcript from authoritative state. It MUST show acceptance, rejection, completion, failure, or unknown outcomes explicitly, and MUST NOT locally invent a successful result when the command response or subsequent Session observation is uncertain.

#### Scenario: A recovery command is rejected
- **WHEN** Compact or Reset is rejected because the Session state changed before the command was applied
- **THEN** the page shows the rejection, refreshes the Session state, and presents only the operations allowed by the refreshed authoritative state
