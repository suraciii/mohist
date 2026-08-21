### Requirement: Current-binding activity-only observations are accepted at session scope

The session-scoped runtime-events endpoint SHALL accept a request without AgentSession or Agent turn identity when the reported runtime session binding is current and every event in the batch is `session.activity`. The append SHALL update the AgentSession activity/transcript state as a session-level fact and SHALL NOT create, update, or emit a Workflow-attributed execution observation. The endpoint SHALL return the normal successful acceptance shape for the submitted activity events.

#### Scenario: A reconnect reports the current Workflow session as idle
- **WHEN** the Runner submits one `session.activity` event without Agent turn identity and its `runtimeSessionId` equals the AgentSession's current runtime binding
- **THEN** the Server SHALL accept and append the activity observation
- **AND** the AgentSession SHALL reflect the reported activity
- **AND** no Workflow execution observation or Workflow task transition SHALL be created from that append

#### Scenario: A reconnect reports the current Workflow session as active
- **WHEN** the Runner submits one current-binding `session.activity` event whose activity is `active` without Agent turn identity
- **THEN** the Server SHALL accept it as a session-level observation
- **AND** the acceptance SHALL NOT require or invent an Agent turn identity
- **AND** the observation SHALL NOT be attributed to any Workflow turn

### Requirement: The relaxed path is limited to pure activity batches

For a Workflow-introduced AgentSession, a request without Agent turn identity SHALL NOT use the session-level relaxation when the batch contains a non-`session.activity` event or mixes activity with any other event. Such a request SHALL be rejected or ignored using the existing runtime-event boundary contract, SHALL NOT append the disallowed events, and SHALL NOT produce a Workflow observation from them.

#### Scenario: An unattributed non-activity event is submitted on a Workflow session
- **WHEN** the session-scoped route receives `message.delta` without Agent turn identity for a current runtime binding on a Workflow-introduced session
- **THEN** the Server SHALL reject or ignore the request according to the existing stale/unattributed runtime-event contract
- **AND** it SHALL NOT append the `message.delta`
- **AND** it SHALL NOT create a Workflow execution observation

#### Scenario: A mixed activity batch is submitted without turn identity
- **WHEN** the session-scoped route receives `session.activity` together with a non-activity event and no Agent turn identity
- **THEN** the Server SHALL reject or ignore the batch as unattributed Workflow runtime data
- **AND** it SHALL NOT partially append the activity event
- **AND** it SHALL NOT append or attribute the non-activity event

### Requirement: Current runtime binding remains a required fence

The session-level activity-only acceptance SHALL require the reported `runtimeSessionId` to match the AgentSession's current physical runtime binding. A stale, missing, or mismatched runtime binding SHALL retain the existing rejection or ignored-result behavior and SHALL NOT mutate the AgentSession or create a Workflow observation.

#### Scenario: A reconnect reports activity for a replaced runtime session
- **WHEN** the Runner submits an activity-only batch with a runtime session id that is no longer current
- **THEN** the Server SHALL reject or ignore the batch according to the existing binding contract
- **AND** it SHALL NOT append the stale activity observation
- **AND** it SHALL NOT change the current AgentSession activity or Workflow state

### Requirement: Workflow-attributed runtime events remain fail-closed

Workflow-attributed runtime events SHALL require the complete acknowledged Agent turn binding: input delivery identity, AgentSession identity, Agent turn identity, TaskRun identity, Work identity, runtime, and the current runtime session binding. The binding SHALL match the persisted Workflow execution binding, and each submitted event that carries turn identity SHALL match the acknowledged Agent turn. Missing, partial, stale, or mismatched bindings SHALL be rejected before append or ignored according to the existing contract; the Server SHALL NOT infer Workflow attribution from a session-level activity observation.

#### Scenario: A Workflow runtime event omits the acknowledged turn binding
- **WHEN** the Workflow session runtime-events route receives `session.activity` or another Workflow runtime event without the complete execution identity
- **THEN** the Server SHALL reject it with the existing fail-closed workflow binding error contract
- **AND** it SHALL NOT append the event
- **AND** it SHALL NOT notify Workflow execution observation handling

#### Scenario: A Workflow runtime event names a replaced AgentSession
- **WHEN** a request supplies a complete-looking Workflow binding whose AgentSession identity does not match the resolved session
- **THEN** the Server SHALL reject it as a stale or changed AgentSession binding
- **AND** it SHALL NOT append the runtime events
- **AND** it SHALL NOT create a Workflow observation for the mismatched execution

#### Scenario: A valid Workflow turn event is submitted
- **WHEN** the request supplies the complete acknowledged binding, the current runtime session id, and event payload turn identities matching the recorded Agent turn
- **THEN** the Server SHALL append the runtime events
- **AND** Workflow observation handling SHALL use that acknowledged execution binding
