### Requirement: Generic AgentSession summaries expose the AgentJob failure reason

The generic AgentSession summary SHALL expose the persisted AgentJob failure reason as `failureReason` whenever the AgentJob ends in failure. The failure reason SHALL be the actionable reason recorded by the AgentJob terminal result or its `session.closed` event, and SHALL be distinct from `failureCategory`. This requirement applies to runner-reported failures and server-originated AgentJob failures, including dispatch exhaustion and report timeout.

#### Scenario: Runner-reported failure carries reason and category

- **WHEN** a Runner reports a failed AgentJob with failure reason `AgentJob requires 'workspace.path' in dispatch variables` and failure category `invalid-input`
- **THEN** the generic AgentSession summary SHALL report status `failed`
- **AND** `failureReason` SHALL equal `AgentJob requires 'workspace.path' in dispatch variables`
- **AND** `failureCategory` SHALL equal `invalid-input`

#### Scenario: Server-originated AgentJob failure carries its reason

- **WHEN** an AgentJob fails because dispatch retries are exhausted or its result report times out
- **THEN** the generic AgentSession summary SHALL expose the persisted dispatch-exhausted or report-timeout reason as `failureReason`
- **AND** SHALL preserve the corresponding `failureCategory` when one is recorded

#### Scenario: Successful session has no failure reason

- **WHEN** an AgentJob completes successfully and its generic AgentSession summary is read
- **THEN** the summary SHALL NOT report a non-empty `failureReason`
- **AND** SHALL NOT fabricate a failure category

### Requirement: The generic AgentSession API preserves failure details

The generic AgentSession summary API SHALL include `failureReason` and `failureCategory` as separate fields in its response. JSON consumers SHALL receive the same failure reason text persisted for the terminal AgentJob; the API SHALL NOT replace that text with only a category or generic status.

#### Scenario: JSON summary includes the persisted reason

- **WHEN** a caller reads a failed generic AgentSession through its summary API
- **THEN** the JSON response SHALL include the persisted `failureReason`
- **AND** the response SHALL include `failureCategory` separately when present

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
