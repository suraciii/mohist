### Requirement: Generic AgentSession summaries expose the AgentJob failure reason

The generic AgentSession summary SHALL expose the persisted AgentJob failure reason as `failureReason` whenever the AgentJob ends in failure. The failure reason SHALL be the actionable reason recorded by the AgentJob terminal result or its `session.closed` event, and SHALL be distinct from `failureCategory`. For a runner report, AgentJob SHALL derive the category in this order: structured `failureCategory` in output JSON, `WorkResult.Error.Code`, then the report status as fallback. This requirement applies to runner-reported failures and server-originated AgentJob failures, including dispatch exhaustion and report timeout.

#### Scenario: Runner-reported failure carries reason and category

- **WHEN** a Runner reports a failed AgentJob with failure reason `AgentJob requires 'workspace.path' in dispatch variables` and failure category `invalid-input`
- **THEN** the generic AgentSession summary SHALL report status `failed`
- **AND** `failureReason` SHALL equal `AgentJob requires 'workspace.path' in dispatch variables`
- **AND** `failureCategory` SHALL equal `invalid-input`

#### Scenario: Structured output category takes precedence

- **WHEN** a failed runner report carries output `failureCategory` `context_exhausted`, error code `runtime-failed`, and status `failed`
- **THEN** the persisted `failureCategory` SHALL equal `context_exhausted`
- **AND** the lower-precedence error code and status SHALL NOT replace it

#### Scenario: Runner error code precedes status fallback

- **WHEN** a failed runner report has no structured output category and carries error code `invalid-input`
- **THEN** the persisted `failureCategory` SHALL equal `invalid-input`
- **AND** report status `failed` SHALL be used only when both structured category and error code are absent

#### Scenario: Server-originated AgentJob failure carries its reason

- **WHEN** an AgentJob fails because dispatch retries are exhausted or its result report times out
- **THEN** the generic AgentSession summary SHALL expose the persisted dispatch-exhausted or report-timeout reason as `failureReason`
- **AND** SHALL preserve the corresponding `failureCategory` when one is recorded

#### Scenario: Successful session has no failure reason

- **WHEN** an AgentJob completes successfully and its generic AgentSession summary is read
- **THEN** the summary SHALL NOT report a non-empty `failureReason`
- **AND** SHALL NOT fabricate a failure category

#### Scenario: Latest terminal fact follows transcript turn order

- **WHEN** a generic AgentSession has terminal facts in two applicable transcript turns and the newer turn's terminal part has a smaller part-local sequence than the older turn's part
- **THEN** the summary SHALL select reason and category from the newer turn
- **AND** SHALL order terminal facts by turn sequence, then part sequence, then part id

### Requirement: The generic AgentSession API preserves failure details

The generic AgentSession summary API SHALL include `failureReason` and `failureCategory` as separate fields in its response. JSON consumers SHALL receive the same failure reason text persisted for the terminal AgentJob; the API SHALL NOT replace that text with only a category or generic status.

#### Scenario: JSON summary includes the persisted reason

- **WHEN** a caller reads a failed generic AgentSession through its summary API
- **THEN** the JSON response SHALL include the persisted `failureReason`
- **AND** the response SHALL include `failureCategory` separately when present

### Requirement: AgentJob terminal facts are durably delivered to AgentSession

Every AgentJob terminal transition SHALL retain a durable pending terminal-delivery record until the associated AgentSession has synchronously persisted one idempotently identified `session.closed` fact. The stable delivery id SHALL be persisted in the terminal payload and used as the transcript part's correlation key so the AgentJob-owned close is identifiable across Session turns. This SHALL apply to runner-reported completion or failure, preflight failure, dispatch exhaustion, report timeout, and forced failure. An AgentJob report or retry that observes terminal state with a pending delivery SHALL retry the same delivery; process or activation loss SHALL NOT clear it.

#### Scenario: Session close fails after AgentJob terminal save

- **WHEN** an AgentJob persists its terminal state and the first attempt to persist `session.closed` fails
- **THEN** the AgentJob SHALL retain the pending terminal delivery durably
- **AND** a durable retry SHALL eventually persist exactly one `session.closed` fact with the original delivery id, status, reason, category, and recorded time

#### Scenario: Activation loss before Session transcript flush

- **WHEN** the Session receives an AgentJob terminal command but loses activation before the terminal transcript fact is durably stored
- **THEN** the AgentJob pending delivery SHALL remain retryable after reactivation
- **AND** the Session SHALL acknowledge success only after the terminal fact is durable

#### Scenario: Terminal report replay repairs pending delivery

- **WHEN** a Runner repeats a result report for an already-terminal AgentJob whose Session delivery is still pending
- **THEN** the AgentJob SHALL retry the stored terminal delivery instead of rejecting the report without repair
- **AND** repeated delivery SHALL NOT create duplicate terminal facts

### Requirement: mo agent session show renders the failure reason

`mo agent session show <sessionId>` SHALL display the failure reason text for a failed generic AgentSession. Table output SHALL present the failure reason separately from the failure category, and JSON output SHALL preserve the server response without omitting either field.

#### Scenario: Table output shows an actionable failure

- **WHEN** an operator runs `mo agent session show <sessionId>` for a failed AgentJob whose persisted reason is `AgentJob requires 'workspace.path' in dispatch variables`
- **THEN** the table output SHALL display that reason text
- **AND** SHALL display the failure category separately when present

#### Scenario: JSON output preserves both failure fields

- **WHEN** an operator runs `mo agent session show <sessionId> -o json` for a failed AgentJob with both a reason and category
- **THEN** the output SHALL contain both `failureReason` and `failureCategory`
- **AND** neither value SHALL be replaced by the generic session status
