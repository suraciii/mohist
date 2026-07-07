### Requirement: Subscription dispatch consumes only the CloudEvent envelope

The subscription dispatch pipeline SHALL consume only the CloudEvent envelope (its Published-Language attributes: `type`, `source`, `subject`, `data`) to match subscriptions, render the response prompt, and launch the Agent. The dispatch handler SHALL NOT depend on, `using`, or reverse-query any business domain model (Workflow domain, Issue domain). Matching SHALL occur exclusively against envelope attributes, with zero business-domain queries.

#### Scenario: Handler matches on envelope attributes only

- **WHEN** a CloudEvent arrives at the subscription dispatch handler
- **THEN** the handler SHALL read only the envelope's `type`, `source`, `subject`, and `data` fields
- **AND** SHALL NOT issue any read against Workflow or Issue domain models to decide whether a subscription matches

### Requirement: Filter expression matches CloudEvent envelope attributes

A subscription's `Filter` SHALL be a single expression evaluated against CloudEvent envelope attributes. The filter semantics SHALL support, on the `type` attribute: exact match, pipe-separated alternatives (`|`, logical OR), `*` (match any type), and `prefix.*` (matches `prefix` and `prefix.<anything>`). The same exact-match semantics SHALL also apply to the `source` and `subject` attributes so that a subscription can constrain the event source (for example to target a specific issue's workflow run) without a separate scope field.

#### Scenario: Exact type match

- **WHEN** a subscription's filter specifies an exact event type and a CloudEvent of exactly that type arrives
- **THEN** the filter SHALL match that event for this subscription

#### Scenario: Pipe-separated alternatives match any listed type

- **WHEN** a subscription's filter lists multiple types separated by `|`
- **THEN** the filter SHALL match any CloudEvent whose type equals one of the listed alternatives

#### Scenario: Star matches any type

- **WHEN** a subscription's filter on type is `*` and any CloudEvent arrives
- **THEN** the filter SHALL match that event

#### Scenario: prefix.* wildcard matches the prefix and its sub-types

- **WHEN** a subscription's filter on type is `prefix.*` and a CloudEvent arrives whose type is `prefix` or begins with `prefix.`
- **THEN** the filter SHALL match that event
- **AND** a CloudEvent whose type merely contains the prefix as a substring but does not equal or prefix-dot it SHALL NOT match

#### Scenario: Source constraint targets a specific run or issue

- **WHEN** a subscription constrains the `source` attribute to a specific run/issue source URI
- **THEN** only CloudEvents whose `source` matches that constraint SHALL match
- **AND** events from other sources SHALL NOT match, even if their type matches

### Requirement: Event-level arbitration selects exactly one Agent per event

For each CloudEvent instance, the dispatch pipeline SHALL select at most one Agent to respond. The selection algorithm SHALL: (1) find all active subscriptions whose filter matches the event; (2) group matched subscriptions by their owning Agent; (3) score each Agent group by the highest subscription priority among its matched subscriptions; (4) select the Agent group with the highest score; (5) within the winning Agent group, select the single subscription with the highest subscription priority. Exactly one (Agent, subscription) pair SHALL be triggered per event. A single event SHALL NOT trigger more than one Agent.

#### Scenario: Highest-priority Agent takes the event

- **WHEN** two Agents each have a matching subscription on the same event, with priorities P_high and P_low (P_high > P_low)
- **THEN** the Agent owning the P_high subscription SHALL be selected
- **AND** the other Agent SHALL NOT be triggered for this event

#### Scenario: Fallback + takeover (low-priority global, high-priority specific)

- **WHEN** Agent A holds a low-priority global subscription matching an event type, and Agent B holds a high-priority subscription matching the same event type plus a source constraint targeting a specific issue's run
- **THEN** an event from that specific run SHALL trigger only Agent B
- **AND** an event of the same type from any other run SHALL trigger only Agent A

#### Scenario: No match means no Agent is triggered

- **WHEN** a CloudEvent arrives and no active subscription's filter matches it
- **THEN** no Agent SHALL be triggered
- **AND** the dispatch SHALL complete without error

### Requirement: Multiple matches within one Agent select one subscription

Because one Agent MAY own multiple subscriptions that all match the same event, the dispatch pipeline SHALL, within the winning Agent group, select exactly one subscription by the highest subscription priority. Triggering more than one subscription for a single event, even within the same Agent, SHALL NOT occur.

#### Scenario: Same Agent, multiple matching subscriptions

- **WHEN** a single Agent owns two active subscriptions whose filters both match the same event, with priorities P1 and P2 (P1 > P2)
- **THEN** exactly the P1 subscription SHALL be triggered for that Agent
- **AND** the P2 subscription SHALL NOT be triggered for the same event

### Requirement: Equal-priority ties are broken deterministically without rejection

When two or more Agent groups tie on the highest subscription priority, or when two or more subscriptions within the winning Agent group tie on priority, the dispatch pipeline SHALL select exactly one using a deterministic, stable tie-break (for example by subscription identity). The system SHALL NOT raise an error, SHALL NOT reject the event, and SHALL NOT block dispatch on a tie. The same set of matched subscriptions for the same event SHALL always resolve to the same selection.

#### Scenario: Tied Agent groups resolve to one Agent deterministically

- **WHEN** two Agents each match the same event with subscriptions of equal priority
- **THEN** the dispatch SHALL select exactly one of them by a deterministic tie-break
- **AND** SHALL NOT reject, error, or block
- **AND** the selection SHALL be reproducible for the same inputs

#### Scenario: Tied subscriptions within an Agent resolve to one deterministically

- **WHEN** one Agent owns two matching subscriptions of equal priority for the same event
- **THEN** the dispatch SHALL select exactly one of them by a deterministic tie-break
- **AND** SHALL NOT reject, error, or block

### Requirement: Response prompt is rendered from envelope-carried variables

When a subscription is selected, the dispatch pipeline SHALL render its `ResponsePrompt` by simple string substitution of variables carried by the CloudEvent envelope. The system SHALL support at minimum the variables `{{workflow_run_id}}` (parsed from the workflow event's `source`), `{{stage}}` (from the workflow event's `data`), and `{{event_type}}` (from the envelope `type`). Rendering SHALL be plain text substitution and SHALL NOT introduce a template engine. The system SHALL NOT provide an `{{issue}}` variable; the Agent SHALL obtain issue identity itself by running the workflow read command.

#### Scenario: Workflow event variables are substituted

- **WHEN** a workflow CloudEvent whose source is `/mohist/workflow-runs/{runId}` and whose data carries `Stage = "plan"` triggers a subscription whose response prompt references `{{workflow_run_id}}`, `{{stage}}`, and `{{event_type}}`
- **THEN** the rendered prompt SHALL contain the concrete run id, the stage value `plan`, and the event type string respectively

#### Scenario: Unsubstituted placeholders left as-is when no envelope value

- **WHEN** a triggered event's envelope does not carry a value for a referenced variable
- **THEN** the system SHALL leave that placeholder token in place or replace it with an empty value, deterministically
- **AND** SHALL NOT fail dispatch

### Requirement: Two-layer prompt composition at launch

When launching an Agent for a subscription trigger, the system SHALL compose the Agent's execution input from two layers: (1) the owning Agent's identity `Instructions` (first layer, defined on the Agent, shared across all its subscriptions), and (2) the rendered subscription `ResponsePrompt` (second layer). The launched Agent session SHALL receive both layers so that identity and per-event reaction are combined. The composition contract SHALL be identical to the existing manual launch composition.

#### Scenario: Subscription launch carries both identity and response prompt

- **WHEN** a subscription triggers its owning Agent
- **THEN** the Agent's identity `Instructions` and the subscription's rendered `ResponsePrompt` SHALL both be present in the execution input
- **AND** the launched session SHALL receive the composed two-layer prompt

### Requirement: Subscription-triggered launch reuses the shared Agent launcher

The subscription dispatch pipeline SHALL launch Agents through the same internal `IAgentLauncher` service used by the manual HTTP launch path (`POST /api/projects/{...}/agents/{...}/sessions`: mint session id → open generic session → build `AgentJobInput` → `SubmitAsync` to the AgentJob grain). The manual HTTP launch behavior, request/response shape, and observable session metadata SHALL remain unchanged by the introduction of subscription-driven launches.

#### Scenario: Subscription launch produces an equivalent session to manual launch

- **WHEN** a subscription triggers an Agent launch
- **THEN** the resulting generic `AgentSession` SHALL be opened, observable, and follow-up/cancel-able through the same read/control paths as a manually launched session for that Agent

#### Scenario: Manual HTTP launch behavior is preserved

- **WHEN** a caller invokes the existing manual launch endpoint after this change
- **THEN** the request and response shape, status codes, and side effects SHALL remain unchanged
- **AND** the manual launch SHALL continue to reject an archived Agent and an empty prompt exactly as before

### Requirement: Triggered Agent pulls its own context and acts through the official approval channel

The dispatch handler SHALL NOT pre-fetch business context (issue, proposal) on behalf of the Agent. The triggered Agent SHALL obtain workflow and issue context itself by running the workflow read command (`mo workflow get <runId>`, which returns the associated issue ref) and the issue read command, and SHALL execute approval actions through the same official approval commands a human uses (`mo workflow approve` / `mo workflow reject`, etc.). The system SHALL NOT construct a dedicated, structured Agent-only approval channel. The adjudication authority SHALL remain with the workflow run; a triggered Agent SHALL NOT bypass any workflow mechanism.

#### Scenario: Handler does not pre-fetch issue context

- **WHEN** a subscription triggers on a workflow event
- **THEN** the handler SHALL NOT query the Issue domain or load issue state to enrich the launch
- **AND** the rendered prompt SHALL carry only envelope-sourced variables

#### Scenario: Agent approves through the official channel

- **WHEN** a triggered Agent decides to approve the workflow run
- **THEN** it SHALL do so by invoking the same approval command/path a human uses
- **AND** the approval SHALL be adjudicated by the workflow run exactly as a human approval would be
- **AND** the system SHALL NOT provide a separate Agent-only approval pathway that bypasses workflow adjudication
