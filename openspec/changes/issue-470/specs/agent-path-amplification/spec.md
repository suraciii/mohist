### Requirement: Agent status exposes the work used to build its response

Both `GET /api/projects/{projectRef}/agent/status` and the issue's literal compatibility path `GET /api/agent/status` SHALL return the same project-scoped status behavior and bounded amplification summary. The compatibility path SHALL resolve its project from `projectId` query first, then `X-Mohist-Project`; it SHALL return 400 `No active project` when neither is present and SHALL NOT aggregate projects. The summary SHALL expose candidate count, actually processed count, transcript records read, database-call count and downstream-call count as non-negative integers measured over the same request scope. The existing canonical fields and route semantics SHALL remain available. Response-local counting SHALL remain active when OTel collection is `off`; only Meter emission and cross-request route aggregation SHALL be disabled in that state.

#### Scenario: Agent status considers more candidates than it returns

- **WHEN** an agent status request considers multiple candidate Session records for active-agent classification but only some contribute to the returned active-agent list
- **THEN** its amplification summary SHALL report the candidate and actually processed counts for that request
- **AND** SHALL report transcript records read and the database and downstream calls made while producing the response

#### Scenario: Agent status has no current agents

- **WHEN** no active agent contributes to an agent status response
- **THEN** the amplification summary SHALL still be present with explicit non-negative counts, including zero transcript records when the path reads no transcript data
- **AND** zero processed results SHALL NOT cause a missing, infinite or misleading amplification value

#### Scenario: Unscoped status compatibility path resolves one project

- **WHEN** a caller requests `/api/agent/status` with `projectId` query or `X-Mohist-Project`
- **THEN** the response SHALL equal the canonical status response for that resolved project, including amplification
- **AND** a request with no project selector SHALL return 400 rather than aggregate globally

### Requirement: Agent activity exposes transcript and fan-out work

Both `GET /api/projects/{projectRef}/agent/activity` and the issue's literal compatibility path `GET /api/agent/activity` SHALL return the same project-scoped activity behavior and bounded amplification summary. The compatibility path SHALL resolve its project from `projectId` query first, then `X-Mohist-Project`; it SHALL return 400 `No active project` when neither is present and SHALL NOT aggregate projects. The summary SHALL expose candidate session count, actually processed session count, transcript records read, database-call count and downstream-call count as non-negative integers measured over the same request scope. The existing activity summary, cards, waiting items, limit behavior and project-scoped route semantics SHALL remain available. Response-local counting SHALL remain active when OTel collection is `off`; only Meter emission and cross-request route aggregation SHALL be disabled in that state.

#### Scenario: Activity assembly reads transcript and workflow data

- **WHEN** an activity request loads candidate sessions, transcript data and downstream workflow status to produce activity cards
- **THEN** its amplification summary SHALL report candidate sessions, processed sessions, transcript records, database calls and downstream calls for that request
- **AND** the counts SHALL use one common request scope so their ratios are directly comparable

#### Scenario: Activity reconciliation narrows processing

- **WHEN** an activity request loads a bounded candidate set and reconciliation removes candidates that must not become cards
- **THEN** the amplification summary SHALL distinguish the candidate count from the actually processed count
- **AND** the existing response limit SHALL continue to bound returned activity cards

#### Scenario: Unscoped activity compatibility path resolves one project

- **WHEN** a caller requests `/api/agent/activity` with `projectId` query or `X-Mohist-Project`
- **THEN** the response SHALL equal the canonical activity response for that resolved project, including amplification and limit behavior
- **AND** a request with no project selector SHALL return 400 rather than aggregate globally

### Requirement: Agent amplification reporting remains bounded

The amplification summary added to agent status and activity SHALL have a fixed shape and SHALL use only fixed-size counters, regardless of the amount of database, transcript or downstream work observed. Cost verification SHALL assert operation counts rather than elapsed wall-clock time so later changes can prove whether historical projects, sessions, transcripts or workflows amplify these paths.

#### Scenario: Unrelated history amplifies current work

- **WHEN** the same current agent status or activity request is executed against datasets with small and large amounts of unrelated historical data
- **THEN** each response SHALL report the operation counts actually observed for its own request
- **AND** the amplification summary itself SHALL remain fixed-size even when those counts increase

#### Scenario: Activity reaches its maximum result bound

- **WHEN** relevant candidate volume exceeds the activity endpoint's maximum result limit
- **THEN** the endpoint SHALL return no more than its existing maximum number of activity cards
- **AND** its amplification summary SHALL remain fixed-size regardless of candidate volume

### Requirement: Amplification signals are operational data only

Agent-path amplification counts SHALL be runtime signals, not Workflow, AgentSession or issue facts. They MUST NOT be persisted as business state, used to decide scheduling or workflow outcomes, or labeled with project, issue, workflow, session or raw-URL identities when emitted as metrics.

#### Scenario: Amplification changes between equivalent requests

- **WHEN** two otherwise equivalent agent reads observe different operational call counts
- **THEN** the differing counts SHALL affect only runtime diagnostics and metrics
- **AND** SHALL NOT alter any Workflow, AgentSession, issue or scheduling decision

#### Scenario: OTel collection is off

- **WHEN** either canonical agent read runs while OTel collection is configured `off`
- **THEN** its amplification object SHALL still contain the actual response-local candidate, processed, transcript, database and downstream counts
- **AND** those counts SHALL NOT be emitted to the Meter or retained in the cross-request route summary

#### Scenario: Agent metrics are exported

- **WHEN** agent-path amplification counts are emitted through the runtime `Meter`
- **THEN** their labels SHALL use only stable bounded dimensions
- **AND** project, issue, workflow, session and raw-URL identities MUST NOT appear as labels
