### Requirement: Agent status selects only active-work candidates

For a project-scoped `/api/agent/status` request, the system SHALL select the candidate Sessions at the persistence boundary before materializing Session state. Every candidate Session's project identity SHALL match the requested project. The candidate set SHALL contain only direct Agent Sessions that can be active and Sessions associated with Workflow Runs that are currently running in that project; completed, failed, cancelled, and otherwise irrelevant historical Sessions SHALL NOT be materialized or considered as candidates.

#### Scenario: Historical Sessions do not enter the status candidate set

- **WHEN** a project has the same active direct or Workflow-backed work together with additional completed, failed, or cancelled historical Sessions
- **THEN** the status candidate count and the number of materialized Session records SHALL be the same as for the active work alone

#### Scenario: An active direct Agent Session is selected

- **WHEN** a project has a direct Agent Session that is currently active
- **THEN** the Session SHALL be selected and considered for the active-agent response

#### Scenario: A Session for a non-running Workflow is excluded

- **WHEN** a Session is associated with a Workflow Run that is not currently running
- **THEN** the Session SHALL NOT be selected or materialized for that project's agent-status request

#### Scenario: A cross-project Workflow reference is excluded

- **WHEN** a Session's project identity differs from the requested project but its Workflow reference names a running Workflow in the requested project
- **THEN** the Session SHALL NOT be selected or materialized for the request

### Requirement: Agent status preserves active-agent visibility

The agent-status response SHALL preserve the existing active-agent content and ordering. A direct Agent Session SHALL appear only while it is active; a Workflow-backed Session SHALL appear only when its associated running Workflow has matching pending work. Sessions that do not meet those conditions SHALL NOT appear.

#### Scenario: Active direct Agent Session remains visible

- **WHEN** an active direct Agent Session is reported by `/api/agent/status`
- **THEN** its existing agent identity, Session identity, progress, and associated context fields SHALL remain present in the response

#### Scenario: Stale direct Agent Session remains hidden

- **WHEN** a direct Agent Session is no longer active
- **THEN** `/api/agent/status` SHALL NOT include that Session in `activeAgents`

#### Scenario: Workflow Session with non-matching pending work remains hidden

- **WHEN** a selected Workflow-backed Session does not match the running Workflow's pending work
- **THEN** `/api/agent/status` SHALL NOT include that Session in `activeAgents`

### Requirement: Workflow status reads are de-duplicated

For one agent-status request, the system SHALL materialize each selected Session at most once and SHALL read the status of each relevant running Workflow Run at most once, regardless of how many selected Sessions reference that Workflow Run.

#### Scenario: Multiple candidate Sessions reference one running Workflow

- **WHEN** multiple selected Sessions reference the same running Workflow Run
- **THEN** the system SHALL perform no more than one Workflow status read for that Workflow Run during the request

### Requirement: Agent-status amplification is truthful and history-bounded

The `amplification` fields returned by `/api/agent/status` SHALL retain their existing response shape. `candidates` SHALL count the selected Session candidates, `processed` SHALL count the active-agent results emitted by the request, and database and downstream call counts SHALL reflect the operations actually performed. With unchanged active work, adding irrelevant historical Sessions SHALL NOT increase these counters.

#### Scenario: Amplification remains stable with irrelevant history

- **WHEN** the same active work is queried first alone and then with thousands of irrelevant historical Sessions in the project
- **THEN** both responses SHALL contain the same active-agent results and the same `candidates`, `processed`, `databaseCalls`, and `downstreamCalls` values

#### Scenario: No active work has explicit zero selection counts

- **WHEN** a project has no active direct Agent Session and no Session associated with a running Workflow
- **THEN** `/api/agent/status` SHALL return `0` for `candidates` and `processed` in `amplification`
