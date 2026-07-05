### Requirement: Issue default model selection without a variant SHALL clear any stored issue variant

When a user picks a coder model for an issue without also choosing a reasoning variant, the issue-scoped `agent.variant` that may have been stored by a previous selection MUST be deleted so the runner resolves the model against the provider default rather than a stale variant suffix. The PATCH issued by `IssueModelSelector.handleSelect` SHALL include `variant: null` alongside `type` and `model`; it MUST NOT send a payload that omits the `variant` key (omission preserves the stale value).

#### Scenario: Issue default chosen without variant after a variant was previously set
- **WHEN** an issue has `agent.model = "provider/old"` and `agent.variant = "high"` persisted, and the user selects `provider/new` from `IssueModelSelector` without picking a variant chip
- **THEN** the PATCH to `/issues/<n>/workflow-profile/variables` SHALL carry `vars.agent = { type: "opencode", model: "provider/new", variant: null }`, and after the server merges it the issue's resolved variables MUST contain `agent.model = "provider/new"` with no `agent.variant` key.

#### Scenario: Issue default chosen without variant when no variant was set
- **WHEN** an issue has `agent.model = "provider/old"` and no `agent.variant`, and the user selects `provider/new` without a variant
- **THEN** the PATCH SHALL still carry `variant: null` (idempotent delete), and the resolved variables MUST remain free of any `agent.variant` key.

### Requirement: Issue stage model selection without a variant SHALL clear the stage-scoped variant

When a user picks a stage override model without choosing a reasoning variant, the stage-scoped `agent.variant` MUST be deleted for that stage. `IssueModelSelector.handleSetStageModel` SHALL issue the stage-scoped PATCH with `variant: null` alongside `type` and `model`; it MUST NOT omit `variant` from the stage-scoped `agent` payload.

#### Scenario: Stage model chosen without variant after a stage variant was previously set
- **WHEN** an issue has stage `plan` variables `agent.model = "provider/old"` and `agent.variant = "max"`, and the user selects `provider/new` for the `plan` stage without picking a variant chip
- **THEN** the PATCH SHALL carry `stages.plan.vars.agent = { type: "opencode", model: "provider/new", variant: null }`, and after the merge the stage's resolved variables MUST contain `agent.model = "provider/new"` with no `agent.variant` key.

#### Scenario: Stage model chosen without variant clears only that stage's variant
- **WHEN** both `plan` and `check` stages have `agent.variant` set and the user re-selects the `plan` stage model without a variant
- **THEN** only the `plan` stage's `agent.variant` SHALL be deleted; the `check` stage's `agent.variant` MUST be preserved unchanged.

### Requirement: Re-selecting the currently stored project default model SHALL clear a stale default variant

Clicking the already-selected project default model row in `AiSettingsSection` is itself a model-setting action and MUST clear any stored default `variant`. The `if (modelId === storedDefaultModel) return` short-circuit SHALL be removed so the mutation always fires with `variant: null` when a model row is chosen.

#### Scenario: User re-clicks the already-selected default model row
- **WHEN** the project default is `provider/x` with `variant = "high"` stored, and the user clicks the `provider/x` row again without choosing a variant
- **THEN** the model update mutation SHALL fire with `{ model: "provider/x", variant: null }`, and the persisted project default MUST end up as `provider/x` with no `variant`.

#### Scenario: User selects a different default model row
- **WHEN** the project default is `provider/x` with `variant = "high"` stored, and the user selects the `provider/y` row without a variant
- **THEN** the mutation SHALL fire with `{ model: "provider/y", variant: null }`, and the persisted project default MUST end up as `provider/y` with no `variant`.

### Requirement: The workflow-profile variables PATCH SHALL keep its null-delete / omitted-preserve semantics

The generic `PATCH .../workflow-profile/variables` merge contract (server `VariableBundle.Patch` / `DeepMerge`) SHALL remain: a JSON `null` value at a leaf MUST delete that key from the base, and a key that is omitted from the overlay MUST be preserved unchanged. This change relies on that contract; it MUST NOT alter it.

#### Scenario: Null leaf deletes the key
- **WHEN** a PATCH overlay sets `vars.agent.variant = null` over a base that has `agent.variant = "high"`
- **THEN** the merged result MUST NOT contain `agent.variant` under `vars.agent`.

#### Scenario: Omitted key is preserved
- **WHEN** a PATCH overlay sets `vars.agent.model` but omits the `variant` key, over a base that has `agent.variant = "high"`
- **THEN** the merged result MUST still contain `agent.variant = "high"` unchanged.

#### Scenario: Null leaf is a no-op when the key is absent
- **WHEN** a PATCH overlay sets `vars.agent.variant = null` over a base that has no `agent.variant`
- **THEN** the merged result MUST remain free of any `agent.variant` key (no null value stored).

### Requirement: The runner SHALL never append a stale variant to the resolved model id

Once a variant has been deleted from the dispatch snapshot, the runner's `resolveRequestedModel` / `composeRequestedModel` MUST NOT append any variant suffix to the model id. A missing or empty `agent.variant` SHALL yield a bare `model` with no `/<variant>` suffix, so the ACP session is never handed a `model/stale_variant` id that does not exist.

#### Scenario: Variant absent in resolved agent config
- **WHEN** the dispatch snapshot resolves `agent.model = "provider/x"` and `agent.variant` is absent (or empty/undefined)
- **THEN** `composeRequestedModel` SHALL return `{ model: "provider/x", source: "agent.model" }` with no `variant` field and no suffix appended to the model id.

#### Scenario: Variant present in resolved agent config
- **WHEN** the dispatch snapshot resolves `agent.model = "provider/x"` and `agent.variant = "high"`
- **THEN** `composeRequestedModel` SHALL return `{ model: "provider/x/high", variant: "high", source: "agent.model" }`, appending the variant suffix only when a non-empty variant is actually present.
