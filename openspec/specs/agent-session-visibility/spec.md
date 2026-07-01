### Requirement: Generic AgentSession queryable by agent identity

The agent-scoped read model SHALL admit queries against a generic (non-workflow) `AgentSession` by the agent identity labels stamped at launch. The agent id label (`mohist.io/agent-id`) and the agent name label (`mohist.io/agent-name`) SHALL be first-class query keys that filter sessions through the indexed label columns rather than falling through to a no-match branch. Querying by agent identity SHALL return only generic `agent-launch` sessions belonging to the resolved Agent profile in the target project, and SHALL NOT return workflow sessions that happen to share the project.

#### Scenario: Query by agent id returns that agent's generic sessions

- **WHEN** a caller queries generic sessions by the `mohist.io/agent-id` label for a resolved Agent profile
- **THEN** the system SHALL return the generic `agent-launch` sessions whose agent id matches
- **AND** SHALL NOT return sessions belonging to a different agent id
- **AND** SHALL NOT return workflow-shaped sessions

#### Scenario: Query by agent name resolves to the same set

- **WHEN** a caller queries generic sessions by the `mohist.io/agent-name` label
- **THEN** the system SHALL return the same agent's generic sessions as an agent-id query would
- **AND** the name-based query SHALL NOT require the caller to already know the agent id

#### Scenario: Agent identity labels are indexed, not rejected

- **WHEN** a generic session is filtered using the `mohist.io/agent-id` or `mohist.io/agent-name` label
- **THEN** the query SHALL resolve against indexed label columns
- **AND** SHALL NOT be treated as an unknown label that matches no rows

### Requirement: Generic AgentSession queryable by source kind and context references

The agent-scoped read model SHALL admit queries against generic sessions by the `mohist.io/source-kind` label (value `agent-launch`) and by the optional `agent-launch/*` context-reference labels (`mohist.io/agent-launch/issue-number`, `mohist.io/agent-launch/epic-number`, `mohist.io/agent-launch/repository`, `mohist.io/agent-launch/workspace-path`). Each of these labels SHALL be a first-class query key resolved against indexed label columns. Querying by a context reference SHALL return only the generic sessions that carry that reference, and SHALL NOT require resolving a workflow run, scope, mount, or supervisor.

#### Scenario: Query by source kind isolates generic sessions

- **WHEN** a caller queries sessions with the `mohist.io/source-kind` label set to `agent-launch`
- **THEN** the system SHALL return only generic (non-workflow) sessions
- **AND** SHALL exclude workflow-shaped sessions

#### Scenario: Query by issue context reference

- **WHEN** a caller queries generic sessions by the `mohist.io/agent-launch/issue-number` label
- **THEN** the system SHALL return the generic sessions that carry that issue reference at launch
- **AND** SHALL NOT require a workflow run to resolve the reference

#### Scenario: Query by epic, repository, or workspace context reference

- **WHEN** a caller queries generic sessions by any of the `mohist.io/agent-launch/epic-number`, `mohist.io/agent-launch/repository`, or `mohist.io/agent-launch/workspace-path` labels
- **THEN** the system SHALL return the generic sessions carrying that reference
- **AND** each label SHALL be resolvable as an indexed query key

#### Scenario: Workflow-shaped lookup keys remain unchanged

- **WHEN** a caller queries sessions using the existing workflow-shaped lookup keys (`mohist.io/project-id`, `mohist.io/source-id`, `mohist.io/session-name`, `mohist.io/work-id`, `mohist.io/work-type`, `mohist.io/stage`, `mohist.io/issue-number`)
- **THEN** the system SHALL resolve them exactly as before this change
- **AND** workflow-session read behavior SHALL remain unchanged

### Requirement: Agent-scoped session list supports status filtering

The agent-scoped list SHALL support filtering by session status in addition to agent identity. A status filter SHALL restrict the result to sessions whose status falls within the requested set, and the status filter SHALL be combinable with the agent-identity and context-reference filters so that all active filters apply together. The status vocabulary SHALL cover at least `running`, `completed`, `failed`, and `stopped`.

#### Scenario: Filter an agent's sessions by status

- **WHEN** a caller requests an agent's sessions with a status filter of `failed`
- **THEN** the result SHALL contain only that agent's generic sessions whose status is `failed`

#### Scenario: Combine status and context filters

- **WHEN** a caller requests an agent's sessions with both a status filter and a context-reference filter
- **THEN** the result SHALL contain only sessions matching every active filter
- **AND** sessions matching only one filter SHALL be excluded

### Requirement: Agent workbench session list shape

An Agent profile SHALL expose its generic sessions grouped by lifecycle state so a workbench surface can present the four states: recent sessions, currently running sessions, failed sessions, and ended (completed/stopped) sessions. The four groupings SHALL be derivable from the agent-scoped list and SHALL together cover every generic session belonging to the agent. A session SHALL appear in exactly one primary state grouping consistent with its status.

#### Scenario: Running sessions are surfaced

- **WHEN** an agent has one or more generic sessions in an active (running) state
- **THEN** the running grouping SHALL include those sessions
- **AND** the grouping SHALL be derivable from the agent-scoped list

#### Scenario: Failed sessions are surfaced

- **WHEN** an agent has one or more generic sessions in a failed state
- **THEN** the failed grouping SHALL include those sessions

#### Scenario: Ended sessions are surfaced

- **WHEN** an agent has generic sessions that have reached a terminal completed or stopped state
- **THEN** the ended grouping SHALL include those sessions

#### Scenario: Recent sessions are surfaced

- **WHEN** an agent has generic sessions regardless of state
- **THEN** the recent grouping SHALL surface the most recently created sessions
- **AND** the grouping SHALL be ordered by recency

### Requirement: Generic AgentSession summary enrichment

The generic-session read path SHALL carry a summary that lets a caller interpret a direct-Agent session without consulting a workflow run. The summary SHALL surface: the resolved Agent profile identity (agent id and agent name), the session status, the created timestamp, the last-activity timestamp, the resolved model, the usage metrics, the failure category (when present), the tool call and tool error counts, and the optional context references (issue, epic, project, repository, workspace path) recorded at launch. The summary SHALL NOT synthesize workflow-only fields (workflow run id, session name, work id, work type, stage) as if the session belonged to a workflow; fields that have no value for a generic session SHALL be absent or null rather than fabricated.

#### Scenario: Summary carries agent identity

- **WHEN** a caller reads a generic session summary
- **THEN** the summary SHALL include the agent id and agent name of the Agent profile that produced the session

#### Scenario: Summary carries status and timing

- **WHEN** a caller reads a generic session summary
- **THEN** the summary SHALL include the session status, the created timestamp, and the last-activity timestamp

#### Scenario: Summary carries resolved model and usage

- **WHEN** a caller reads a generic session summary
- **THEN** the summary SHALL include the resolved model and the usage metrics for the session

#### Scenario: Summary carries failure category and tool counts

- **WHEN** a caller reads a generic session summary
- **THEN** the summary SHALL include the failure category when present
- **AND** SHALL include the tool call count and tool error count

#### Scenario: Summary carries recorded context references

- **WHEN** a generic session was launched with optional context references (issue, epic, repository, workspace path)
- **AND** a caller reads its summary
- **THEN** the summary SHALL surface the context references recorded at launch

#### Scenario: Generic summary does not fabricate workflow fields

- **WHEN** a caller reads a generic session summary for a session that does not belong to a workflow run
- **THEN** the summary SHALL NOT present a fabricated workflow run id, session name, work id, work type, or stage
- **AND** any workflow-shaped field that has no value for a generic session SHALL be absent or null

#### Scenario: Unknown generic session is rejected

- **WHEN** a caller reads a generic session summary for a session id that does not resolve to a generic `agent-launch` session in the project
- **THEN** the read SHALL fail with a clear not-found result
- **AND** SHALL NOT return a workflow session even if the id accidentally matches

### Requirement: Direct Agent sessions appear as Agent activity with agent attribution

A direct-Agent (generic `agent-launch`) session SHALL appear in the project activity feed as Agent activity attributed to its Agent profile, rather than being mis-attributed to a synthetic issue card. The activity read model SHALL carry the agent identity for generic sessions, and SHALL NOT synthesize an `issue_{projectId}_0` (or any issue-number-zero) card to represent a generic session that has no issue reference. A generic session that carries an issue context reference MAY be associated with that issue, but the activity attribution SHALL still reflect the Agent profile, not a fabricated issue.

#### Scenario: Generic session is attributed to its agent profile

- **WHEN** a generic `agent-launch` session appears in the activity feed
- **THEN** the activity entry SHALL carry the agent id and agent name of the producing Agent profile
- **AND** SHALL NOT be attributed to a synthetic issue-number-zero card

#### Scenario: Generic session with no issue reference produces no synthetic issue card

- **WHEN** a generic session with no issue context reference appears in the activity feed
- **THEN** the activity read model SHALL NOT synthesize an `issue_{projectId}_0` identity for it
- **AND** the entry SHALL be attributable by agent identity alone

#### Scenario: Generic session with an issue reference is associated but agent-attributed

- **WHEN** a generic session carries an issue context reference and appears in the activity feed
- **THEN** the entry MAY be associated with the referenced issue
- **AND** the entry's attribution SHALL still reflect the Agent profile that produced the session

### Requirement: Direct Agent sessions included in active-agents readout

The active-agents readout SHALL include generic `agent-launch` sessions that are currently active, and SHALL NOT exclude records solely because they have a blank workflow run id or work id. The active-agents entry for a generic session SHALL attribute the session to its Agent profile and SHALL NOT require a workflow-run-derived work item to report progress. The active-agents readout SHALL convey AgentSession *visibility* only — which sessions are currently shown and can enter transcript or activity — and SHALL NOT be consumed as the source of capacity active-slot counts; capacity used/max slots SHALL be sourced from the `runner-capacity` projection instead. Workflow-session entries in the active-agents readout SHALL remain unchanged.

#### Scenario: Active generic session appears in active-agents

- **WHEN** a generic `agent-launch` session is currently active
- **THEN** the active-agents readout SHALL include it
- **AND** SHALL NOT exclude it for having a blank workflow run id or work id

#### Scenario: Generic active-agent entry is agent-attributed

- **WHEN** the active-agents readout includes a generic session
- **THEN** the entry SHALL attribute the session to its Agent profile
- **AND** SHALL NOT require a workflow-run-derived work item to report progress

#### Scenario: Active-agents readout conveys visibility, not capacity

- **WHEN** a capacity readout (used/max slots) is computed for any surface
- **THEN** the active-agents readout count SHALL NOT be the source of the capacity active-slot count
- **AND** capacity SHALL be sourced from the `runner-capacity` projection instead

#### Scenario: Workflow active-agent entries are preserved

- **WHEN** the active-agents readout includes workflow sessions
- **THEN** those entries SHALL behave exactly as before this change
- **AND** their workflow-derived progress SHALL remain unchanged

### Requirement: Issue and epic references surface as lightweight session associations

A generic session that records an issue or epic context reference at launch SHALL surface as a lightweight association on the referenced issue or epic, so a reader of that issue or epic can discover the related Agent session and navigate back to it. The association SHALL be a read-only link from the referenced entity to the session, SHALL link back to the session identity, and SHALL NOT create scope, mount, supervisor, ownership, or workflow-lifecycle relationships. The association SHALL be derivable by querying generic sessions by the relevant `agent-launch/*` context-reference label.

#### Scenario: Issue reference surfaces as an association

- **WHEN** a generic session records an issue context reference at launch
- **THEN** the referenced issue SHALL surface the session as a lightweight association
- **AND** the association SHALL link back to the session identity
- **AND** SHALL NOT create scope, mount, supervisor, or workflow lifecycle

#### Scenario: Epic reference surfaces as an association

- **WHEN** a generic session records an epic context reference at launch
- **THEN** the referenced epic SHALL surface the session as a lightweight association
- **AND** the association SHALL link back to the session identity

#### Scenario: Association is read-only and creates no lifecycle

- **WHEN** a generic session references an issue or epic
- **THEN** the association SHALL be read-only
- **AND** SHALL NOT alter the issue or epic's state, ownership, or workflow lifecycle
- **AND** removing the reference SHALL NOT affect the session's execution

#### Scenario: Sessions without an issue or epic reference produce no association

- **WHEN** a generic session is launched without an issue or epic context reference
- **THEN** no issue or epic SHALL surface a session association for it
- **AND** the session SHALL remain observable only through the agent-scoped read paths
