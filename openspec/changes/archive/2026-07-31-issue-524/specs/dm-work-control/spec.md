### Requirement: The Owner can cancel or stop work from the DM

The Owner SHALL be able to cancel a queued Turn or request a stop of an executing Turn from within the DM conversation. These operations SHALL be available only to the Connection Owner and SHALL reuse Mohist's existing Turn control qualification — cancel applies to a queued Turn, stop applies to an executing Turn — without establishing a separate control mechanism or vocabulary for the Slack surface.

#### Scenario: Owner cancels queued work from the DM

- **WHEN** the Owner cancels a queued Turn from the DM conversation
- **THEN** the queued Turn is cancelled and the Bot reports the cancellation

#### Scenario: Owner requests a stop of executing work from the DM

- **WHEN** the Owner requests a stop of an executing Turn from the DM conversation
- **THEN** a runtime stop request is issued for that Turn and the Bot reports the stop outcome

#### Scenario: Cancel of an executing Turn is redirected to stop

- **WHEN** the Owner cancels a Turn that is currently executing
- **THEN** the operation indicates that the Turn is executing and that a stop is required rather than performing a deterministic cancel

### Requirement: Cancel and stop act on a single identified Turn

A cancel or stop from the DM SHALL identify exactly one target AgentTurn and SHALL affect only that Turn. A stop request SHALL apply only to the Turn identified at the time the request is issued and SHALL NOT carry over to any Turn that begins afterward.

#### Scenario: Only the targeted Turn is affected

- **WHEN** a cancel or stop is issued from the DM carrying a specific Turn identity
- **THEN** only the Turn with that identity is evaluated
- **AND** no other Turn in any session is cancelled or stopped

### Requirement: An expired control entry does not stop later work

When a cancel or stop entry targets a Turn that has already reached a terminal state (completed, failed, cancelled, or stopped), the operation SHALL report that the work has ended and SHALL NOT cancel or stop any Turn that began after the target Turn ended. In particular, a stale stop entry SHALL NOT stop a Turn that is currently executing under a later session or a later Turn in the same session.

#### Scenario: Stale stop entry does not stop newer work

- **WHEN** the Owner invokes a stop entry whose target Turn has already ended, while a later Turn is now executing
- **THEN** the operation reports that the target work has ended
- **AND** the currently executing Turn is neither stopped nor cancelled

#### Scenario: Stale cancel entry reports the work has ended

- **WHEN** the Owner invokes a cancel entry whose target Turn has already reached a terminal state
- **THEN** the operation reports that the work has ended and issues no cancel or stop
