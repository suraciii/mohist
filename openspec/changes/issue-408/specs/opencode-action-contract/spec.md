### Requirement: `mohist/opencode` has a narrow explicit input

Selecting `uses: mohist/opencode` SHALL define Action Input as a required `prompt` that resolves to non-empty text, an optional logical `session` name, and an optional `options` object. The input MUST NOT require `agent`, `kind`, or `type`; task-level `expect` MUST remain outside the Action Input. Selecting this Action SHALL invoke an inline agent turn and MUST NOT resolve a predefined Mohist Agent or select an OpenCode agent by name.

#### Scenario: A prompt-only task is valid

- **WHEN** a task selects `mohist/opencode` and supplies only a `prompt` that resolves to non-empty text
- **THEN** the Action Input SHALL be valid
- **AND** no model, variant, agent identity, or backend discriminator SHALL be required

#### Scenario: Legacy agent input is invalid

- **WHEN** a `mohist/opencode` task supplies `agent`, `kind`, `type`, or Workflow completion policy inside `with`
- **THEN** validation SHALL reject the invalid input with the offending field identified
- **AND** the Action MUST NOT interpret that field as configuration

### Requirement: OpenCode options are explicit and limited to model selection

`options` SHALL accept optional string fields `model` and `variant`. Only values explicitly present in `with.options` SHALL affect the turn. Omitting `options` SHALL leave model selection to the current OpenCode Session or OpenCode default and MUST NOT consult `vars.agent` as fallback. Keys other than `model` and `variant` SHALL have no execution effect, SHALL be ignored with a diagnostic, and MUST NOT make an otherwise valid turn fail. A present non-string `model` or `variant` SHALL fail input validation actionably.

#### Scenario: Explicit options configure the turn

- **WHEN** Action Input contains `options: { "model": "provider/model", "variant": "high" }`
- **THEN** the Action contract SHALL expose that model and variant as the turn's explicit model selection

#### Scenario: Omitted options do not inherit hidden Variables

- **WHEN** Action Input omits `options` while effective Workflow Variables contain `vars.agent`
- **THEN** the Action SHALL receive no model or variant from those Variables
- **AND** the Variables MUST NOT alter the turn through fallback behavior

#### Scenario: Transitional extra option keys are inert

- **WHEN** an explicitly bound options object also contains a legacy key such as `type` or a liveness setting
- **THEN** the Action SHALL ignore that key and record a diagnostic
- **AND** the key MUST NOT affect execution or fail the turn

#### Scenario: An option has the wrong type

- **WHEN** `options.model` or `options.variant` is present with a non-string value
- **THEN** the Action SHALL fail input validation with the invalid field identified
- **AND** no turn SHALL start

### Requirement: Model identifiers split only at the first slash

A non-empty `options.model` SHALL use `provider/modelID` form with non-empty provider and model-ID portions. The provider SHALL be the substring before the first `/`; the complete remainder SHALL be the model ID, including any additional `/` characters. `variant` SHALL remain a sibling option and MUST NOT be appended to or parsed from the model identifier.

#### Scenario: A model ID contains additional slashes

- **WHEN** `options.model` is `openrouter/vendor/family/model`
- **THEN** the provider SHALL be `openrouter`
- **AND** the model ID SHALL be `vendor/family/model`

#### Scenario: Provider or model ID is missing

- **WHEN** `options.model` has no `/` or the text before or after its first `/` is empty
- **THEN** input validation SHALL reject the model identifier with a `provider/model` error
- **AND** no turn SHALL start

#### Scenario: Variant remains independent

- **WHEN** `options.variant` is supplied with or without `options.model`
- **THEN** the variant SHALL remain a distinct string option
- **AND** it MUST NOT change how provider and model ID are split

### Requirement: OpenCode Action Output is minimal and promise-specific

The public Action Output for `mohist/opencode` SHALL be `null` unless Workflow completion matches a configured promise marker. When a promise marker matches, Workflow completion SHALL project exactly `{ "promise": "<value>" }`, using the value inside the matched `<promise>` marker. The Action and execution runtime MUST NOT evaluate `expect` or synthesize this output themselves.

#### Scenario: No promise marker is matched

- **WHEN** an OpenCode turn completes without a configured promise marker match
- **THEN** Action Output SHALL be `null`

#### Scenario: A promise marker is matched

- **WHEN** Workflow completion matches `<promise>FAIL</promise>` for an OpenCode task
- **THEN** Action Output SHALL equal `{ "promise": "FAIL" }`
- **AND** recovery SHALL be able to match `promise=FAIL`

### Requirement: Runtime and completion facts stay out of OpenCode Action Output

Runtime Session identity, model observations, usage, transcript text, provider errors, diagnostics, expectation details, and the final assistant text fact SHALL remain in their owning runtime, Session, diagnostic, or task state. They MUST NOT be copied into `mohist/opencode` Action Output. Task failure detail SHALL remain available without turning those facts into business-output fields.

#### Scenario: A completed turn reports runtime facts

- **WHEN** an OpenCode turn produces a Runtime Session ID, model and usage observations, transcript text, and diagnostics
- **THEN** those facts SHALL be recorded through their owning channels
- **AND** Action Output SHALL remain `null` or the minimal promise object only

#### Scenario: `_output` completion reads final text privately

- **WHEN** Workflow completion evaluates an `_output` marker against the turn's final assistant text
- **THEN** the final text SHALL be supplied as a private result fact
- **AND** it MUST NOT be added to Action Output

### Requirement: Model configuration writers use only model and variant semantics

Built-in defaults and Project or Issue model-selection write paths SHALL represent `vars.agent` using only `model` and optional `variant` meanings. Model-selection write paths SHALL accept a model identifier whose model-ID portion contains additional `/` characters and SHALL reject identifiers without a non-empty provider and model-ID portion. New writes MUST NOT add `type`, runtime names, liveness settings, or a Mohist Agent identity. Existing persisted extra keys do not become valid Action configuration; when an explicit whole-object binding carries them temporarily, they SHALL remain inert under the options contract.

#### Scenario: A Project or Issue selects a model and variant

- **WHEN** a model-selection surface writes `vars.agent` for a selected model and variant
- **THEN** the written configuration SHALL contain the model and variant values
- **AND** it MUST NOT add `type: opencode` or other execution-backend keys

#### Scenario: A model-selection writer accepts a multi-segment model ID

- **WHEN** a Project or Issue model-selection surface writes `openrouter/vendor/family/model`
- **THEN** validation SHALL accept the identifier and persist it unchanged
- **AND** it SHALL treat `openrouter` as the provider and `vendor/family/model` as the model ID

#### Scenario: A model-selection writer rejects a missing portion

- **WHEN** a Project or Issue model-selection surface receives `model-only`, `/model`, or `provider/`
- **THEN** validation SHALL reject the value with an actionable `provider/model` error
- **AND** the invalid model selection MUST NOT be persisted

#### Scenario: A built-in profile has no model override

- **WHEN** a built-in profile declares its default `variables.agent` without selecting a model
- **THEN** the default SHALL contain no backend discriminator or liveness configuration
- **AND** agent tasks SHALL still bind `options` explicitly where effective model configuration is intended
