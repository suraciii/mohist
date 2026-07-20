### Requirement: A routed Agent launch uses the triggering workflow workspace

After a routing rule matches and selects a Named Agent, the real dispatch path SHALL resolve the execution workspace identified by the triggering event's workflow or issue lineage. When the event carries a workflow run id whose persisted run has a workspace, the launch SHALL use that workspace path for both the AgentSession work directory and the AgentJob dispatch. When the event identifies an issue but not a workflow run, the launch SHALL use the workspace of that issue's current workflow run when one exists. The Runner SHALL receive a non-empty `workspace.path` for every such routed AgentJob.

#### Scenario: Failed workflow event launches in its persisted workspace

- **WHEN** a routing rule matches a `com.mohist.workflow.run.failed` event carrying a workflow run id and issue number, and that WorkflowRun has a persisted workspace
- **THEN** the triggered AgentSession work directory SHALL equal the WorkflowRun workspace path
- **AND** the AgentJob dispatch SHALL carry the same non-empty path as `workspace.path`
- **AND** the Runner SHALL start the Agent turn instead of rejecting it for a missing workspace

#### Scenario: Issue lineage resolves the current workflow workspace

- **WHEN** a routing rule matches an event carrying an issue number but no workflow run id, and the issue has a current WorkflowRun with a persisted workspace
- **THEN** the triggered Agent SHALL run in that WorkflowRun workspace
- **AND** the AgentJob dispatch SHALL NOT omit `workspace.path`

### Requirement: Missing routed workspace fails explicitly

A matched routing hit for which no execution workspace can be resolved SHALL NOT submit a malformed AgentJob dispatch with an absent or empty `workspace.path`. The routed outcome SHALL be recorded as failed with an actionable failure reason that identifies the missing or unavailable workspace, and SHALL retain the triggering event id and routing rule id so the operator can diagnose the hit.

#### Scenario: Workflow run has no resolvable workspace

- **WHEN** a routing rule matches an event whose workflow or issue lineage does not resolve to an execution workspace
- **THEN** the system SHALL NOT dispatch an AgentJob with an absent or empty `workspace.path`
- **AND** the routed outcome SHALL expose a failure reason identifying that an execution workspace could not be resolved
- **AND** the failure SHALL remain correlated with the triggering event id and routing rule id

### Requirement: Matching and prompt rendering remain envelope-only

Workspace resolution SHALL occur only after a rule has matched and its response prompt has been rendered. Match evaluation, rule ordering, `continue` behavior, and response-prompt rendering SHALL continue to use only the CloudEvent envelope and SHALL NOT depend on a Workflow, Issue, AgentSession, or workspace lookup. For the same event and routing table, real dispatch and `mo routing test` SHALL continue to select the same rules and Agents; workspace availability affects execution after selection, not matching.

#### Scenario: Workspace lookup does not affect rule selection

- **WHEN** the same event is evaluated by `mo routing test` and by real dispatch against the same routing table
- **THEN** both paths SHALL select the same matched rules and response Agents
- **AND** real dispatch SHALL resolve workspace context only after those selections are made

#### Scenario: Prompt rendering does not read workspace state

- **WHEN** a matched rule renders its response prompt from `{{event.*}}` placeholders
- **THEN** every substitution SHALL be derived from the event envelope
- **AND** workspace resolution SHALL NOT add non-envelope values to prompt rendering

### Requirement: Routed failures are traceable from their triggering issue

For a routed launch whose event carries issue lineage, the resulting AgentSession SHALL record the issue reference in addition to the triggering event id and routing rule id. If the AgentJob fails, the associated issue event feed SHALL expose a terminal AgentSession outcome containing enough information to identify the AgentSession, response Agent, triggering event, routing rule, and failure reason. The failure SHALL NOT be observable only by separately discovering and opening the AgentSession.

#### Scenario: Routed AgentJob failure appears in the issue event feed

- **WHEN** an event for issue 42 triggers a Named Agent through a routing rule and the resulting AgentJob fails
- **THEN** `mo issue events 42` SHALL include the routed AgentSession's failed terminal outcome
- **AND** the outcome SHALL identify the AgentSession and response Agent
- **AND** the outcome SHALL expose the failure reason and its triggering event id and routing rule id

#### Scenario: Routed session retains trigger and issue correlation

- **WHEN** a routing rule launches a Named Agent for an event carrying issue lineage
- **THEN** the resulting AgentSession SHALL retain the issue number, triggering event id, and routing rule id
- **AND** those values SHALL remain available after the AgentJob reaches a terminal state

### Requirement: Existing non-routing workspace behavior is preserved

This change SHALL NOT alter the workspace supplied by the manual Named Agent launch path when the caller provides one, and SHALL NOT alter workspace preparation or selection for Inline Agent execution through the `mohist/opencode` action.

#### Scenario: Manual launch keeps its supplied workspace

- **WHEN** a caller launches a Named Agent manually with an explicit workspace path
- **THEN** the AgentSession and AgentJob SHALL continue to use that supplied path
- **AND** routing workspace resolution SHALL NOT replace it

#### Scenario: Inline Agent workspace is unchanged

- **WHEN** a Workflow executes an Inline Agent through the `mohist/opencode` action
- **THEN** its workspace SHALL continue to be prepared and selected by the existing Workflow execution path
- **AND** routing workspace resolution SHALL NOT participate in that execution
