### Requirement: Agent status exposes the work used to build its response

`GET /api/projects/{projectRef}/agent/status` SHALL include a bounded amplification summary for that response. The summary SHALL expose candidate count, actually processed count, transcript records read, database-call count and downstream-call count as non-negative integers measured over the same request scope, so a caller can directly compare the work performed with the result produced. The existing agent status fields and project-scoped route semantics SHALL remain available.

#### Scenario: Agent status considers more candidates than it returns

- **WHEN** an agent status request considers multiple candidate runner or active-agent records but only some contribute to the returned status
- **THEN** its amplification summary SHALL report the candidate and actually processed counts for that request
- **AND** SHALL report transcript records read and the database and downstream calls made while producing the response

#### Scenario: Agent status has no current agents

- **WHEN** no active agent contributes to an agent status response
- **THEN** the amplification summary SHALL still be present with explicit non-negative counts, including zero transcript records when the path reads no transcript data
- **AND** zero processed results SHALL NOT cause a missing, infinite or misleading amplification value

### Requirement: Agent activity exposes transcript and fan-out work

`GET /api/projects/{projectRef}/agent/activity` SHALL include a bounded amplification summary for that response. The summary SHALL expose candidate session count, actually processed session count, transcript records read, database-call count and downstream-call count as non-negative integers measured over the same request scope. These counts SHALL make visible when a small activity response requires disproportionate transcript, database or cross-component work. The existing activity summary, cards, waiting items, limit behavior and project-scoped route semantics SHALL remain available.

#### Scenario: Activity assembly reads transcript and workflow data

- **WHEN** an activity request loads candidate sessions, transcript data and downstream workflow status to produce activity cards
- **THEN** its amplification summary SHALL report candidate sessions, processed sessions, transcript records, database calls and downstream calls for that request
- **AND** the counts SHALL use one common request scope so their ratios are directly comparable

#### Scenario: The activity limit narrows processing

- **WHEN** an activity request applies its response limit to a larger candidate set
- **THEN** the amplification summary SHALL distinguish the candidate count from the actually processed count
- **AND** the existing response limit SHALL continue to bound returned activity cards

### Requirement: Agent amplification cost follows current relevant work

The agent status and activity paths SHALL keep their response and diagnostic memory bounded. Their database, transcript and downstream work SHALL depend on current relevant candidates and the existing response bound, not on unrelated historical projects, sessions, transcripts or workflows. Cost verification SHALL compare operation counts with and without unrelated history; it MUST NOT use elapsed wall-clock time as the assertion.

#### Scenario: Unrelated history increases

- **WHEN** the same current agent status or activity request is executed against datasets with small and large amounts of unrelated historical data
- **THEN** the number of records inspected and downstream calls SHALL remain within the same explicit bound
- **AND** the amplification summary and response size SHALL remain bounded

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

#### Scenario: Agent metrics are exported

- **WHEN** agent-path amplification counts are emitted through the runtime `Meter`
- **THEN** their labels SHALL use only stable bounded dimensions
- **AND** project, issue, workflow, session and raw-URL identities MUST NOT appear as labels
