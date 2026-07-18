### Requirement: The model catalog is loaded structurally through the read-only v2 list APIs

The Runner SHALL load the provider and model catalog through `client.v2.provider.list()` and `client.v2.model.list()` (the same read-only APIs the OpenCode TUI uses) and SHALL report the model/variant catalog on Runner registration for configuration hints in the Server and Web. The Runner MUST NOT discover the catalog by shelling out to the OpenCode CLI (for example `opencode models --verbose`) or by parsing command output. The catalog is a configuration aid and SHALL NOT be the final authority on model legality or defaults.

#### Scenario: The catalog is loaded from the v2 list APIs

- **WHEN** the Runner starts and passes readiness
- **THEN** the catalog SHALL be loaded via `client.v2.provider.list()` and `client.v2.model.list()`
- **AND** the Runner MUST NOT parse `opencode models` CLI output to discover models

#### Scenario: The catalog informs configuration but not legality

- **WHEN** the Server or Web presents model choices
- **THEN** it SHALL use the catalog reported by the Runner as a configuration hint
- **AND** final model legality and defaults SHALL remain OpenCode's judgment

### Requirement: OpenCode is the final authority on model validity and defaults

When `options.model` is omitted, the runtime SHALL leave model selection to the current OpenCode Session selection or the OpenCode default. Whether a selected model is valid SHALL be judged by OpenCode; the runtime SHALL NOT pre-validate model legality beyond the `provider/modelID` shape. Omitting `options` SHALL preserve the current Session selection, and on first selection with no choice present SHALL use the OpenCode default.

#### Scenario: Omitted options keep the current selection

- **WHEN** a turn is run without `options` on a Session that already has a current selection
- **THEN** the runtime SHALL preserve that selection
- **AND** SHALL NOT substitute a different model

#### Scenario: OpenCode decides whether a model is valid

- **WHEN** a turn supplies a syntactically well-formed `provider/modelID` that OpenCode does not support
- **THEN** the runtime SHALL pass it to OpenCode
- **AND** the validity outcome SHALL come from OpenCode, not from Mohist pre-validation

### Requirement: The runtime splits a model identifier only at the first slash and keeps variant independent

A non-empty `options.model` SHALL use `provider/modelID` form with non-empty provider and model-ID portions. The provider SHALL be the substring before the first `/`; the complete remainder, including any additional `/` characters, SHALL be the model ID. `options.variant` SHALL remain a sibling option and MUST NOT be appended to or parsed from the model identifier. The runtime SHALL construct the SDK model DTO from this parsed provider and model ID inside the module boundary.

#### Scenario: A model ID with additional slashes keeps the full remainder

- **WHEN** `options.model` is `openrouter/vendor/family/model`
- **THEN** the runtime SHALL treat the provider as `openrouter`
- **AND** SHALL treat the model ID as `vendor/family/model`

#### Scenario: Variant does not change the split

- **WHEN** `options.variant` is supplied with or without `options.model`
- **THEN** the variant SHALL remain a distinct value
- **AND** SHALL NOT alter how provider and model ID are split

#### Scenario: A malformed model identifier is rejected

- **WHEN** `options.model` has no `/` or the text before or after its first `/` is empty
- **THEN** the runtime SHALL reject the identifier with a `provider/model` error
- **AND** no turn SHALL start

### Requirement: Model and variant are applied per turn without rotating the physical Session

When creating a new physical Session, the runtime SHALL pass the explicit model to Session creation and to the first prompt. When reusing an existing physical Session, the runtime SHALL carry the specified model and variant on that prompt; the mature Session API SHALL update the Session selection on user-message creation, requiring no separate switch call. A change of model or variant SHALL NOT enter the Session cache key, SHALL NOT gate resume, and SHALL NOT rotate the physical Session.

#### Scenario: A new session receives the explicit model

- **WHEN** a turn creates a new physical Session with an explicit `options.model`
- **THEN** the runtime SHALL pass the model to Session creation and to the first prompt

#### Scenario: A reused session applies model/variant per prompt

- **WHEN** a turn reuses an existing physical Session with a new `options.model` or `options.variant`
- **THEN** the runtime SHALL carry the model and variant on that prompt
- **AND** SHALL NOT create or rotate a physical Session as a result

### Requirement: Unknown option keys are inert and type-invalid options fail actionably

Keys in `options` other than `model` and `variant` SHALL have no execution effect, SHALL be ignored, and SHALL be recorded as diagnostics; they MUST NOT make an otherwise valid turn fail. This SHALL backstop persisted `vars.agent` that still contains legacy keys such as `type` or liveness configuration. A present `options.model` or `options.variant` that is not a string SHALL fail with an actionable input error, and no turn SHALL start.

#### Scenario: Legacy option keys are ignored with a diagnostic

- **WHEN** an explicitly bound `options` object also contains a legacy key such as `type` or a liveness setting
- **THEN** the runtime SHALL ignore that key and record a diagnostic
- **AND** the key MUST NOT affect execution or fail the turn

#### Scenario: A non-string option fails validation

- **WHEN** `options.model` or `options.variant` is present with a non-string value
- **THEN** the runtime SHALL fail with an actionable input error identifying the invalid field
- **AND** no turn SHALL start
