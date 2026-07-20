### Requirement: A routed Agent launch uses the triggering workflow workspace

After a routing rule matches and selects a Named Agent, the real dispatch path SHALL resolve an execution context identified by the triggering event's workflow or issue lineage. The resolved WorkflowRun SHALL belong to the event's project and, when the event carries issue or epic lineage, SHALL match that lineage. When the event carries a workflow run id whose persisted run has a non-empty workspace path, the launch SHALL use that path for both the AgentSession work directory and the AgentJob dispatch. When the event identifies an issue but not a workflow run, the launch SHALL use the workspace of that issue's currently bound, nonterminal WorkflowRun. The Runner SHALL receive a non-empty `workspace.path` for every such routed AgentJob.

#### Scenario: Failed workflow event launches in its persisted workspace

- **WHEN** a routing rule matches a `com.mohist.workflow.run.failed` event carrying a workflow run id and issue number, and that WorkflowRun has a persisted workspace
- **THEN** the triggered AgentSession work directory SHALL equal the WorkflowRun workspace path
- **AND** the AgentJob dispatch SHALL carry the same non-empty path as `workspace.path`
- **AND** the Runner executor SHALL proceed past required-workspace validation rather than rejecting the dispatch for an absent `workspace.path`

#### Scenario: Issue lineage resolves the current workflow workspace

- **WHEN** a routing rule matches an event carrying an issue number but no workflow run id, and the issue is currently bound to a nonterminal WorkflowRun with a persisted non-empty workspace path
- **THEN** the triggered Agent SHALL run in that WorkflowRun workspace
- **AND** the AgentJob dispatch SHALL NOT omit `workspace.path`

### Requirement: Missing routed workspace fails explicitly

A matched routing hit for which no valid execution context can be resolved SHALL NOT submit a malformed AgentJob dispatch with an absent or empty `workspace.path`. Missing runs, project/issue/epic lineage mismatches, null or whitespace workspace paths, and issue-only references to terminal or stale runs SHALL produce this outcome. The routed outcome SHALL be recorded as failed with an actionable failure reason that identifies the missing or invalid workspace context, and SHALL retain the triggering event id and routing rule id so the operator can diagnose the hit.

#### Scenario: Workflow run has no resolvable workspace

- **WHEN** a routing rule matches an event whose workflow or issue lineage does not resolve to an execution workspace
- **THEN** the system SHALL NOT dispatch an AgentJob with an absent or empty `workspace.path`
- **AND** the routed outcome SHALL expose a failure reason identifying that an execution workspace could not be resolved
- **AND** the failure SHALL remain correlated with the triggering event id and routing rule id

#### Scenario: Explicit WorkflowRun lineage mismatch is rejected

- **WHEN** a routing rule matches an event whose workflow run belongs to a different project or conflicts with the event's issue or epic lineage
- **THEN** the system SHALL NOT launch the Agent in that WorkflowRun workspace
- **AND** the routed outcome SHALL fail with a reason identifying the lineage mismatch

#### Scenario: Issue-only terminal or stale run is not reused

- **WHEN** a routing rule matches an issue event with no workflow run id and the issue has no currently bound nonterminal WorkflowRun with a non-empty workspace path
- **THEN** the system SHALL NOT reuse a retained terminal or stale run workspace
- **AND** the routed outcome SHALL fail explicitly without creating a Runner assignment

### Requirement: Routed launch preparation is first-writer fenced

An idempotent routed launch SHALL persist one canonical launch plan, keyed by project id, event id, and rule id, before opening its AgentSession or making work dispatchable. The plan SHALL include the AgentJob input, Session launch metadata and work directory, and either an executable disposition or a preflight failure. Redelivery SHALL reuse that persisted plan and SHALL NOT merge newly resolved workspace or lineage values into the Session or AgentJob.

#### Scenario: Unresolved first delivery remains canonical

- **WHEN** the first delivery of an event-rule pair persists a workspace-unavailable launch plan and a later delivery can resolve a workspace
- **THEN** the later delivery SHALL reuse the original failed plan
- **AND** SHALL NOT add the later workspace to the AgentSession or dispatch the AgentJob

#### Scenario: Prepared launch is not dispatched before Session open

- **WHEN** a routed executable launch plan has been persisted but its AgentSession has not yet been durably opened from that plan
- **THEN** the AgentJob SHALL remain non-dispatchable
- **AND** redelivery SHALL complete the same Session open before enabling dispatch

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

### Requirement: Routed failure events have stable envelope and ordering semantics

The issue event feed SHALL project each failed routed AgentSession as exactly one CloudEvent-shaped `session.closed` entry. The selected transcript part SHALL be the AgentJob-owned terminal fact whose persisted delivery id and correlation key equal `agent-job:{jobKey}:terminal`; Runtime-owned, follow-up, or otherwise unrelated close parts SHALL NOT be projected as routing outcomes. Its numeric `id` SHALL be the source-local terminal transcript-part id; `eventId` SHALL be the stable value `{sessionId}:closed:{terminalDeliveryId}`; `source` SHALL be the canonical AgentSession source; `subject` SHALL be the Session id; `time` SHALL be the terminal part's persisted last-seen time; `specVersion` SHALL be `1.0`; and `dataContentType` SHALL be `application/json`. Extensions SHALL include canonical project and issue lineage. Data SHALL preserve terminal delivery id, status, exit code, failure reason/category and add Session id, Agent id/name, trigger event id, and trigger rule id.

The feed SHALL select the newest global `limit` entries across Issue, valid WorkflowRun, and routed AgentSession sources, then return those entries in ascending order. Both selection and output SHALL use one total ordering key: time, origin rank (`issue` before `workflow-run` before `agent-session`), source ordinal, source-local numeric id, then event id ordinal. Equal timestamps and colliding numeric ids across stores SHALL therefore remain deterministic.

#### Scenario: Projected routed failure has complete stable envelope

- **WHEN** a routed AgentSession for issue 42 persists an AgentJob-owned failed terminal part with id 9 and delivery id `agent-job:job-1:terminal`
- **THEN** the issue feed SHALL contain one `session.closed` entry whose `eventId` is `{sessionId}:closed:agent-job:job-1:terminal` and whose time comes from that part
- **AND** the entry SHALL carry the specified source, subject, spec version, content type, project/issue extensions, terminal data, Agent identity, and trigger correlation

#### Scenario: Unrelated Session closes do not duplicate a routed failure

- **WHEN** a routed AgentSession contains its AgentJob-owned failed terminal part plus failed Runtime or follow-up close parts in other turns
- **THEN** the issue feed SHALL project exactly one routed failure entry selected by the AgentJob terminal delivery id
- **AND** SHALL NOT project the unrelated close parts as routing outcomes

#### Scenario: Limit selects newest entries globally

- **WHEN** Issue, WorkflowRun, and routed AgentSession sources together contain more entries than the requested limit
- **THEN** the feed SHALL select the newest entries across all sources before returning results
- **AND** SHALL return the selected entries in ascending total-key order

#### Scenario: Equal-time cross-store entries are deterministic

- **WHEN** entries from different stores have equal timestamps and colliding numeric ids
- **THEN** repeated reads SHALL return the same order determined by origin rank, source, numeric id, and event id

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
