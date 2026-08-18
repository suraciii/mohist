### Requirement: Canonical reasoning-effort vocabulary

The system SHALL treat `reasoningEffort` as a canonical execution-configuration
value that is independent from `variant`. A configured value MUST be one of
`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`; an absent or null
value means unset. An effort MUST NOT be encoded as a variant, and a variant
MUST NOT be interpreted as an effort.

#### Scenario: Canonical values are accepted

- **WHEN** an Agent definition sets `agentConfig.reasoningEffort` to any of
  `off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`
- **THEN** the write surface MUST accept the value and persist it verbatim

#### Scenario: Non-canonical value is rejected

- **WHEN** `agentConfig.reasoningEffort` is set to a value outside the
  canonical set (for example `extreme`)
- **THEN** the write surface MUST reject the configuration with an actionable
  error that names the accepted values
- **AND** no partial configuration may be persisted

#### Scenario: Effort and variant are independent

- **GIVEN** a model that has both a true variant and a reasoning effort
- **WHEN** the Agent selects effort `high` and variant `balanced`
- **THEN** the stored configuration MUST keep both values in separate fields
- **AND** neither value may be derived from, encoded as, or validated against
  the other

#### Scenario: Unset effort is valid

- **WHEN** an Agent definition omits `reasoningEffort` or sets it to null
- **THEN** the configuration MUST be accepted
- **AND** launches MUST proceed without applying any reasoning effort

### Requirement: Write surfaces accept reasoningEffort

The Agent-definition `agentConfig` (Server API validation, `mo agent
create/update`, and the Web Agent profile editor) and the issue-level
agent-config override SHALL accept `reasoningEffort`. The issue-level override
MUST NOT gain the Agent-owned `runtime` key.

#### Scenario: Server API persists the effort

- **WHEN** an Agent is created or updated with
  `agentConfig.reasoningEffort: "high"` alongside `model` and `variant`
- **THEN** the API MUST accept the request and persist the effort in the
  Agent's config

#### Scenario: Wrong type or empty value is rejected

- **WHEN** `agentConfig.reasoningEffort` is not a string or null, or is an
  empty string
- **THEN** validation MUST reject it under the same string-or-null, non-empty
  rules applied to `model` and `variant`

#### Scenario: Issue-level override carries the effort

- **WHEN** an Issue's `agentConfig` override sets `reasoningEffort`
- **THEN** the Issue write surface MUST accept the key beside `model` and
  `variant`
- **AND** the stored issue profile MUST forward the effort into the
  `vars.agent` execution options consumed by workflow agent tasks

### Requirement: CLI exposes the effort on agent create, update, and view

`mo agent create` and `mo agent update` SHALL provide `--reasoning-effort` and
a mutually exclusive `--clear-reasoning-effort`; `mo agent view` SHALL render
the stored effort.

#### Scenario: Create with an effort

- **WHEN** `mo agent create` runs with `--reasoning-effort high`
- **THEN** the created Agent's config MUST contain `reasoningEffort: high`
- **AND** `mo agent view` MUST display the effort

#### Scenario: Update clears the effort

- **WHEN** `mo agent update` runs with `--clear-reasoning-effort`
- **THEN** the stored effort MUST be removed without touching `model`,
  `variant`, or `runtime`
- **AND** passing both `--reasoning-effort` and `--clear-reasoning-effort`
  MUST fail with a usage error

#### Scenario: Invalid effort is rejected locally

- **WHEN** `--reasoning-effort` is given a value outside the canonical set
- **THEN** the CLI MUST reject it before sending the request, listing the
  accepted values

### Requirement: Web exposes effort as its own control

The Web Agent profile editor and model pickers SHALL present reasoning effort
as a control separate from the variant control, driven by the runtime catalog's
per-model `reasoningEfforts` — never by the `variants` map.

#### Scenario: Effort options come from the catalog's reasoningEfforts

- **GIVEN** the selected runtime and model report
  `reasoningEfforts: [low, medium, high]`
- **WHEN** the effort control is rendered
- **THEN** it MUST offer exactly those values
- **AND** it MUST NOT offer values from the model's `variants` map

#### Scenario: Choosing an effort writes the canonical key

- **WHEN** a user selects effort `high` in the picker and saves the Agent
- **THEN** the Agent's `agentConfig` MUST record `reasoningEffort: high`
- **AND** the `variant` value, if any, MUST remain unchanged

#### Scenario: Selection without effort support offers nothing

- **GIVEN** the selected model or runtime reports no `reasoningEfforts` or
  `supportsReasoningEffort=false`
- **WHEN** the editor renders the effort control for that selection
- **THEN** it MUST offer no effort values
- **AND** it MUST NOT save an effort for that selection

### Requirement: Web agent surfaces display the stored effort

The Web Agent list rows and the Agent detail configuration card SHALL display
the stored `reasoningEffort` beside the model, with the true variant still
shown separately. An absent effort MUST NOT be displayed or synthesized on
either surface.

#### Scenario: List rows show the effort beside model

- **GIVEN** an Agent whose config carries model `m`, effort `high`, and a true
  variant `balanced`
- **WHEN** the Agent list renders the Agent's row
- **THEN** the row MUST display `high` beside `m`
- **AND** `balanced` MUST still be displayed as its own value

#### Scenario: Detail config card shows the effort

- **GIVEN** an Agent whose config carries model `m`, effort `high`, and a true
  variant `balanced`
- **WHEN** the Agent detail page renders the Agent Config card
- **THEN** the card MUST display the effort `high` beside the Model entry
- **AND** the Variant entry MUST still display `balanced` separately
- **AND** the card's edit-timing note MUST name the reasoning effort among the
  configuration keys whose edits apply only to Jobs created after saving

#### Scenario: Absent effort displays as nothing

- **GIVEN** an Agent whose config carries no `reasoningEffort`
- **WHEN** the list row and the detail config card render
- **THEN** neither surface may display an effort value
- **AND** no default effort value may be synthesized

### Requirement: Every launch path freezes the effort in the execution snapshot

Every durable execution snapshot — `AgentJobInput`, `RoutedAgentLaunchPlan`,
the `WorkDispatch` `with` payload, `AgentExecutionDefinition`, and
session-target definitions — SHALL freeze the tuple `(runtime, model,
reasoningEffort, variant)` together with the resolved capability revision. A
frozen snapshot MUST NOT be rewritten by later Agent edits or later catalog
changes.

#### Scenario: Resolved snapshots carry the effort

- **WHEN** an AgentJob is prepared on any launch path (manual HTTP,
  subscription/routed launch, mention, or workflow task)
- **THEN** each snapshot MUST carry the resolved effort beside model and
  variant
- **AND** the dispatch `with` payload MUST deliver the effort to the runner,
  which MUST NOT re-read the Agent definition to obtain it

#### Scenario: Editing the Agent does not change prepared work

- **GIVEN** an AgentJob was prepared with effort `high`
- **WHEN** the Agent's effort is edited to `low` or the Agent is deleted
- **THEN** the in-flight job MUST continue to execute with the frozen effort
  `high`

#### Scenario: Catalog changes never rewrite a frozen snapshot

- **GIVEN** a dispatch was frozen with effort `high` at capability revision R
- **WHEN** the runtime catalog later changes
- **THEN** the frozen snapshot MUST retain effort `high` and revision R
  unchanged

### Requirement: Agent Readiness includes the effort

Agent Readiness SHALL compare the resolved reasoning effort when matching a
past execution against the current Agent definition, and SHALL surface effort
misconfiguration as setup gaps.

#### Scenario: Effort mismatch breaks the definition match

- **GIVEN** the latest execution ran with effort `high` and the Agent now
  defines effort `low`
- **WHEN** readiness is evaluated
- **THEN** that execution MUST NOT count as matching the current definition

#### Scenario: Effort without a model is a setup gap

- **WHEN** an Agent sets `reasoningEffort` without setting a model
- **THEN** readiness MUST report a Needs setup gap with actionable guidance

#### Scenario: Execution-configuration failures map to Needs setup

- **GIVEN** the latest execution failed with category
  `unsupported_execution_configuration` or
  `incompatible_execution_configuration`
- **WHEN** readiness is evaluated
- **THEN** it MUST conclude Needs setup with guidance to update the Agent's
  execution configuration

### Requirement: Pi thinking-level variants are removed with no compatibility layer

Pi registration MUST stop publishing thinking levels as variants, and a
variant MUST NOT be applied as a Pi thinking level. Stored Agent configs whose
Pi `variant` value is actually a thinking level become invalid execution
configurations: the system MUST NOT migrate, reinterpret, or alias them, and
the value MUST be re-entered as `reasoningEffort` to restore execution.

#### Scenario: A saved thinking-level variant is not silently honored

- **GIVEN** a stored Agent config with `runtime: pi` and `variant: high` (a Pi
  thinking level, not a true variant)
- **AND** a complete Pi catalog that lists no such variant
- **WHEN** the Agent is launched
- **THEN** the launch MUST NOT apply `high` as a thinking level through the
  variant field
- **AND** the configuration MUST be rejected explicitly, with the frozen tuple
  recorded, rather than executed or silently dropped

#### Scenario: No migration or aliasing is performed

- **WHEN** this change is deployed
- **THEN** stored variant values MUST NOT be rewritten or reinterpreted as
  efforts
- **AND** re-entering the value as `reasoningEffort` MUST be required to
  restore execution

### Requirement: Execution evidence records the applied effort

Execution evidence — the session's recorded model facts and the AgentJob
terminal result — SHALL record the reasoning effort that was actually applied,
distinct from model and variant.

#### Scenario: Applied effort is recorded

- **WHEN** a turn completes with frozen effort `high`
- **THEN** the session model facts MUST record `high` as the applied effort
- **AND** the AgentJob terminal output MUST carry the effort beside model and
  variant

#### Scenario: Absent effort is not synthesized

- **WHEN** a turn completes without a frozen effort
- **THEN** the evidence MUST record the effort as absent
- **AND** it MUST NOT synthesize a default effort value
