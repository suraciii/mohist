### Requirement: Manual and event-subscription launches share a single launch pipeline

Both the manual launch HTTP route (`POST /api/projects/{projectRef}/agents/{agentRef}/sessions`) and the event-subscription dispatch handler SHALL enter through one `IAgentLauncher.LaunchAsync` pipeline. The pipeline SHALL resolve the Agent identity, open the canonical AgentSession, capture the launch-time-fixed Agent snapshot, submit the AgentJob, and dispatch the Agent-owned execution request. There SHALL be no source-specific execution fork beyond the launch entry itself.

#### Scenario: Manual launch enters the shared pipeline

- **WHEN** a caller POSTs a prompt to the manual launch route with a valid Agent reference
- **THEN** the route SHALL resolve the Agent and invoke `IAgentLauncher.LaunchAsync` with no trigger labels
- **AND** the pipeline SHALL open the AgentSession, submit the AgentJob, and dispatch the Agent-owned execution request

#### Scenario: Subscription dispatch enters the shared pipeline

- **WHEN** a CloudEvent matches an active Agent subscription and arbitration picks a winner
- **THEN** the dispatch handler SHALL invoke `IAgentLauncher.LaunchAsync` with the rendered response prompt and the trigger labels (event id, subscription id)
- **AND** the pipeline SHALL open the AgentSession, submit the AgentJob, and dispatch the Agent-owned execution request through the same code path as manual launch

### Requirement: The launch pipeline resolves a stable Agent identity

Both launch entries SHALL resolve a stable Agent identity before opening an AgentSession or submitting an AgentJob. Manual launch SHALL resolve the Agent reference (id or name) via the project-scoped Agent resolver; subscription dispatch SHALL resolve the winning subscription's owning Agent. A launch SHALL be rejected before any state is created when the resolved Agent is archived (manual) or not active (subscription). The resolved `AgentInfo` is the source of the launch-time-fixed snapshot.

#### Scenario: Manual launch rejects an archived Agent

- **WHEN** the manual launch route resolves an Agent whose status is `Archived`
- **THEN** the route SHALL reject the launch with an `agent_archived` error before any AgentSession or AgentJob is created

#### Scenario: Subscription dispatch skips inactive Agents

- **WHEN** a CloudEvent matches a subscription whose owning Agent is not `Active`
- **THEN** the dispatch handler SHALL skip that subscription before arbitration
- **AND** SHALL NOT create an AgentSession or AgentJob for it

#### Scenario: Manual launch rejects an unknown Agent reference

- **WHEN** the manual launch route cannot resolve the Agent reference by name or id
- **THEN** the route SHALL reject the launch with a not-found error before any state is created

### Requirement: The AgentSession is opened at launch with the canonical source shape

The launch pipeline SHALL open the AgentSession with `runtime: "opencode"`, source kind `agent-launch`, and source labels carrying at minimum `project-id`, `agent-id`, and `agent-name`. The subscription path SHALL additionally merge trigger labels `trigger/event-id` and `trigger/subscription-id` onto the session metadata. The Runner id at open time SHALL be empty; the Runner SHALL stamp itself when it accepts the dispatch. Manual launch SHALL mint a fresh random session id; subscription launch SHALL mint a deterministic session id derived from the project, event, and subscription identity so a redelivered event resolves to the same session.

#### Scenario: Manual launch mints a fresh session id

- **WHEN** the manual launch pipeline opens an AgentSession
- **THEN** the session id SHALL be a fresh random id
- **AND** the session metadata SHALL carry `source-kind=agent-launch`, `agent-id`, `agent-name`, and `project-id`
- **AND** the metadata SHALL NOT carry trigger labels

#### Scenario: Subscription launch mints a deterministic session id

- **WHEN** the subscription launch pipeline opens an AgentSession for a given (project, event, subscription) tuple
- **THEN** the session id SHALL be a deterministic function of that tuple
- **AND** a redelivery of the same event for the same subscription SHALL resolve to the same session id

#### Scenario: Subscription launch stamps trigger labels

- **WHEN** the subscription launch pipeline opens an AgentSession
- **THEN** the session metadata SHALL carry `trigger/event-id` and `trigger/subscription-id`
- **AND** those labels SHALL be immutable after creation

#### Scenario: The opened session declares the OpenCode runtime

- **WHEN** the launch pipeline opens an AgentSession for either source
- **THEN** the session SHALL be opened with `runtime: "opencode"`
- **AND** the Runner id SHALL be empty at open time

### Requirement: The launch captures a launch-time-fixed Agent snapshot

The launch pipeline SHALL capture the resolved `AgentInfo` snapshot — Agent id, instructions, and Agent config — verbatim into the submitted AgentJob input. The persisted AgentJob state SHALL carry this snapshot independently of the live Agent definition so concurrent edits do not affect the in-flight job.

#### Scenario: Instructions are snapshotted at launch

- **WHEN** the launch pipeline submits an AgentJob
- **THEN** the AgentJob input SHALL carry the resolved Agent's instructions verbatim
- **AND** a subsequent edit to the Agent's instructions SHALL NOT change the persisted AgentJob snapshot

#### Scenario: Agent config is snapshotted at launch

- **WHEN** the launch pipeline submits an AgentJob
- **THEN** the AgentJob state SHALL persist the resolved Agent's config as an independent snapshot
- **AND** a subsequent edit to the Agent's config SHALL NOT change the persisted AgentJob snapshot

### Requirement: Event, subscription, AgentJob, and AgentSession are bidirectionally traceable

The launch pipeline SHALL produce stable, queryable links among the triggering event, the matching subscription, the AgentJob, and the AgentSession. The AgentJob SHALL carry the AgentSession id on its input. The AgentSession SHALL carry the Agent id (and, for subscription launches, the trigger labels) on its metadata. The AgentSession id for a subscription launch SHALL be derivable from the (project, event, subscription) tuple so the inverse lookup is deterministic.

#### Scenario: The AgentJob points to its AgentSession

- **WHEN** the launch pipeline submits an AgentJob
- **THEN** the AgentJob input SHALL carry the AgentSession id
- **AND** the AgentJob grain SHALL use that id to close the session on terminal transitions

#### Scenario: The AgentSession points to its Agent and triggers

- **WHEN** the launch pipeline opens an AgentSession
- **THEN** the session metadata SHALL carry the Agent id label
- **AND** subscription-launch metadata SHALL additionally carry the trigger event id and subscription id labels

#### Scenario: A triggering event resolves back to its session

- **WHEN** a caller looks up an AgentSession by (project id, event id, subscription id) after a subscription launch
- **THEN** the lookup SHALL deterministically resolve to the same session id the launch produced

### Requirement: Submission semantics differ by source but share one grain contract

Manual launch SHALL submit the AgentJob via a strict submission that rejects a divergent re-submission to an already-started grain. Subscription launch SHALL submit via an idempotent submission that no-ops when a grain with the same deterministic key already exists. Both submissions SHALL target the same `IAgentJobGrain` contract; the idempotency difference SHALL come from the submission method, not from a per-source grain shape.

#### Scenario: Manual launch rejects a divergent re-submission

- **WHEN** a manual launch targets an AgentJob grain that has already started with a different input
- **THEN** the submission SHALL reject with a validation error
- **AND** SHALL enumerate the field-level differences

#### Scenario: Subscription launch is idempotent per (project, event, subscription)

- **WHEN** a subscription launch resolves to an AgentJob grain whose deterministic key already exists
- **THEN** the submission SHALL be a no-op
- **AND** SHALL NOT create a second AgentJob or AgentSession

### Requirement: Subscription arbitration picks one winner per triggering event

Subscription dispatch SHALL list every active subscription for the resolved project whose filter matches the CloudEvent and whose owning Agent is active. Arbitration SHALL group candidates by Agent, pick the highest-priority subscription within each group (tie-broken by lex-smaller subscription id), then pick the winning group by highest group score (tie-broken by lex-smaller top subscription id). Exactly one Agent SHALL be launched per matching CloudEvent.

#### Scenario: Highest priority wins within an Agent group

- **WHEN** two matching subscriptions target the same Agent with different priorities
- **THEN** the arbitration SHALL pick the higher-priority subscription as the group's candidate

#### Scenario: Single winner across Agent groups

- **WHEN** matching subscriptions span multiple Agents
- **THEN** arbitration SHALL pick exactly one Agent group's candidate
- **AND** exactly one Agent SHALL be launched

#### Scenario: Priority ties break by lex-smaller subscription id

- **WHEN** two matching candidates share the highest priority
- **THEN** arbitration SHALL pick the candidate with the lex-smaller subscription id

### Requirement: The subscription response prompt is rendered from a fixed placeholder set

The response prompt rendered for a subscription launch SHALL substitute at most the placeholders `{{workflow_run_id}}`, `{{stage}}`, and `{{event_type}}` from the triggering CloudEvent. Unrecognized placeholders SHALL be left verbatim. A rendered prompt that is empty or whitespace SHALL skip the dispatch without creating an AgentSession or AgentJob.

#### Scenario: Recognized placeholders are substituted

- **WHEN** the response prompt template contains `{{workflow_run_id}}`, `{{stage}}`, or `{{event_type}}`
- **THEN** the renderer SHALL substitute each from the corresponding CloudEvent extension or type

#### Scenario: Unrecognized placeholders are left verbatim

- **WHEN** the response prompt template contains a placeholder outside the recognized set
- **THEN** the renderer SHALL leave it verbatim in the prompt sent to the Agent

#### Scenario: An empty rendered prompt skips dispatch

- **WHEN** the rendered response prompt is empty or whitespace
- **THEN** the dispatch handler SHALL skip launch
- **AND** SHALL NOT create an AgentSession or AgentJob
