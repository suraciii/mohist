### Requirement: One history record per execution

The Agent execution history SHALL contain exactly one record per AgentJob and one record per AgentTurn that is not bound to a represented AgentJob. A record's identity SHALL be its Job identity for Job records and its Session plus Turn identity for Turn records. An AgentTurn bound to an AgentJob that already has a record MUST NOT produce a second record for the same execution, and a single history view MUST NOT present the same record more than once.

#### Scenario: Agent launch produces one record

- **WHEN** a Mohist Agent launch creates an AgentJob, an AgentSession, the first SessionInput, and the first AgentTurn
- **THEN** the history SHALL contain exactly one record for that execution, identified by the Job identity
- **AND** the Job-bound first Turn MUST NOT appear as an additional separate record

#### Scenario: Follow-up turn produces its own record

- **WHEN** a Follow-up input opens a new AgentTurn on an existing AgentSession without creating a new AgentJob
- **THEN** the history SHALL contain one additional record identified by that Session and Turn
- **AND** the record SHALL carry its own task summary, outcome, and timing

#### Scenario: History view contains no duplicates

- **WHEN** a history view is rendered for an Agent
- **THEN** no execution SHALL appear more than once in that view
- **AND** grouping or filtering of the view MUST NOT cause the same record to be listed in two sections simultaneously

### Requirement: History records carry task, outcome, result, context, timing, model, and cost

Each history record SHALL carry: a task summary derived from the SessionInputs that opened the execution; the outcome status; a result summary with the result message or output summary and the failure reason when the execution failed; the launch context references (Issue, Epic, repository, workspace) recorded for the Session; the start and end timestamps and the duration; the resolved model; and cost from recorded usage. Fields whose authoritative fact does not exist MUST be absent rather than fabricated.

#### Scenario: Completed execution with a terminal result

- **WHEN** an AgentJob or AgentTurn has completed with a recorded terminal result
- **THEN** the record SHALL show the completed status together with the result summary
- **AND** the record SHALL carry the start time, end time, and computed duration

#### Scenario: Failed execution exposes its failure reason

- **WHEN** an execution failed with a recorded failure reason or failure category
- **THEN** the record SHALL present the failure outcome with its failure reason
- **AND** the failure vocabulary SHALL be the same one the Session page and the result export use for that execution

#### Scenario: Session without launch context

- **WHEN** the Session carried no Issue, Epic, repository, or workspace reference at launch
- **THEN** the record's context field SHALL be absent
- **AND** the surface MUST NOT fabricate or default a context reference

#### Scenario: In-flight execution has no terminal fields

- **WHEN** an execution is pending, queued, or executing
- **THEN** the record SHALL present its nonterminal status with start time and absent end time and duration
- **AND** it MUST NOT fabricate a result summary, end time, or duration

### Requirement: Cost and model carry honest attribution

A history record SHALL present usage cost and model only at the attribution level the recorded facts actually support. When usage is recorded at Session level rather than per Turn or per Job, the record SHALL label that cost as Session-level attribution and MUST NOT present it as the measured cost of one Turn or one Job.

#### Scenario: Only session-level usage exists

- **WHEN** the Server has recorded usage and cost for the Session as a whole but not per Turn
- **THEN** the record SHALL display that cost explicitly labeled as Session-level
- **AND** it MUST NOT divide, allocate, or otherwise fabricate a per-Turn cost figure

#### Scenario: Model resolved at session level

- **WHEN** the model is known only as the Session's resolved model
- **THEN** the record SHALL present that model without claiming a per-Turn model measurement

### Requirement: History is a read-only projection of authoritative lifecycle facts

The history SHALL be a read-only projection of existing lifecycle facts. It MUST NOT re-arbitrate or mutate AgentJob, AgentTurn, or AgentSession state, and it MUST NOT introduce new transcript facts. A record's status SHALL be the authoritative status of its owning Job or Turn; an `unknown` status SHALL render as unknown and MUST NOT be presented as failed, and an AgentJob result SHALL stay distinct from AgentSession Activity.

#### Scenario: Unknown job is not presented as failed

- **WHEN** an AgentJob's authoritative status is `unknown`
- **THEN** the history record SHALL present unknown
- **AND** no history surface MAY label, group, or icon that record as failed

#### Scenario: Job result stays distinct from session activity

- **WHEN** an AgentJob completed while its AgentSession's Activity is `unknown` or `active`
- **THEN** the record SHALL present the Job's completed result
- **AND** it MUST NOT derive or overwrite the Session's Activity from the Job outcome

#### Scenario: Failed job with continuing session

- **WHEN** an AgentJob failed but its AgentSession remains open and accepts Follow-ups
- **THEN** the history record SHALL present the Job failure
- **AND** it MUST NOT present the Session itself as failed or ended

### Requirement: Server exposes the history through a read API

The Server SHALL expose the Agent execution history as a project-scoped, agent-scoped read endpoint that returns history records ordered by recency, supports a status filter and a result limit consistent with the existing list conventions, and enforces project isolation. The existing jobs and sessions list endpoints SHALL remain available unchanged for their current consumers.

#### Scenario: Reading an Agent's history

- **WHEN** a caller reads the history endpoint for a resolved Agent in a Project
- **THEN** the response SHALL return history records carrying the full record contract
- **AND** the records SHALL be ordered by most recent start or activity first

#### Scenario: Filtering by status

- **WHEN** the caller supplies a status filter
- **THEN** only records whose authoritative status matches the filter SHALL be returned
- **AND** a filter value outside the accepted status vocabulary SHALL be rejected

#### Scenario: Existing list endpoints remain

- **WHEN** a current consumer reads the existing agent jobs list or agent sessions list endpoints
- **THEN** those endpoints SHALL continue to return their existing contracts without the history projection replacing them

### Requirement: Web Agent detail page presents history records

The Web Agent detail page SHALL replace its session-row list with the Agent execution history: one distinguishable record per execution, each showing at least the task summary, outcome and result summary, launch context, timing, model, and attributed cost. Records SHALL link into their Session page anchored to the corresponding Turn. The page MUST NOT group `unknown` executions under a Failed section.

#### Scenario: History section renders distinguishable records

- **WHEN** the Agent detail page loads an Agent with past executions
- **THEN** the history section SHALL render one row per execution, distinguishable by task summary, result, and context
- **AND** no execution SHALL appear in more than one section of the page

#### Scenario: Unknown execution is not labeled Failed

- **WHEN** an execution's authoritative status is `unknown`
- **THEN** the page SHALL present it as unknown with unknown-styled presentation
- **AND** it MUST NOT appear under a Failed grouping or with a failure icon

#### Scenario: Record links to the anchored Session page

- **WHEN** the user activates a history record
- **THEN** the page SHALL navigate to that record's Session page anchored to the corresponding Turn
- **AND** the anchored Session page SHALL retain the anchor after a page refresh

### Requirement: CLI reads return the history contract

The CLI SHALL expose the Agent execution history through its `mo` read commands so a table or `--json` read returns task summary, outcome and result summary, launch context, timing, model, and attributed cost — not bare lifecycle timestamps. Each record SHALL identify its Session (and Turn for Turn records) so the user can navigate to the Session.

#### Scenario: Table output shows the execution story

- **WHEN** the user runs the Agent history or job read command for an Agent
- **THEN** the table output SHALL include columns for task, outcome and result summary, context, timing, model, and attributed cost
- **AND** an `unknown` status SHALL render as unknown rather than failed

#### Scenario: JSON output carries the full contract

- **WHEN** the user runs the same read with `--json`
- **THEN** the JSON output SHALL carry the history record contract fields, with absent facts omitted rather than nulled or fabricated

#### Scenario: CLI record navigates to the Session

- **WHEN** a CLI history record identifies its execution
- **THEN** the output SHALL include the Session identity and, for Turn records, the Turn identity
- **AND** the user SHALL be able to open that Session with the existing `mo session` view command using the displayed identity
