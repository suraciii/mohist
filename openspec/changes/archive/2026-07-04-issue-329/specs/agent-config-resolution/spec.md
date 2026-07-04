### Requirement: Agent config originates solely from the agent object

`ConfigService.GetAgentConfigAsync` SHALL resolve the global agent configuration exclusively from the `agent` configuration key. The legacy fallback path that synthesized an agent config (`{ ["model"] = model }`) from the deprecated single-field `model` configuration key MUST be removed. A config that defines only `model` (and no `agent` object) SHALL produce no agent configuration.

#### Scenario: agent object yields the agent configuration

- **WHEN** `config.jsonc` defines an `agent` object (for example `{ "model": "gpt-4o", "type": "opencode" }`)
- **THEN** `GetAgentConfigAsync` returns that object as the agent configuration

#### Scenario: model-only config no longer synthesizes an agent configuration

- **WHEN** `config.jsonc` defines only `model` (for example `anthropic/claude`) and no `agent` object
- **THEN** `GetAgentConfigAsync` returns null (no agent configuration is produced)

#### Scenario: Neither agent nor model yields no agent configuration

- **WHEN** `config.jsonc` defines neither `agent` nor `model`
- **THEN** `GetAgentConfigAsync` returns null

### Requirement: GetVariables no longer synthesizes an agent from legacy model

`ConfigService.GetVariables` builds its `vars.agent` exclusively from `GetAgentConfigAsync`. Because the legacy `model` fallback is removed, `GetVariables` SHALL return an empty `VariableBundle` when only `model` is configured (with no `agent` object), rather than exposing a synthesized `agent`.

#### Scenario: model-only config yields an empty bundle

- **WHEN** `GetVariables` is called with only the `model` key set (no `agent` object)
- **THEN** the returned `VariableBundle` has a null `Vars` (no synthesized `agent` entry)
- **AND** the returned `VariableBundle` has null `Stages`

#### Scenario: agent object is still exposed at vars.agent

- **WHEN** `GetVariables` is called with an `agent` object configured
- **THEN** the returned bundle's `Vars` contains the agent configuration nested under `agent`
- **AND** does not leak a top-level `model` key sibling to `agent`

### Requirement: model key and clearing are retired from the agent path

The `model` entry SHALL be removed from the configuration schema, and the `ClearAsync("model")` call retained inside `SetAgentModelAsync` purely to avoid legacy shadowing SHALL be removed, since the legacy single-field `model` fallback no longer exists. `SetAgentModelAsync` SHALL continue to write the model under the unified `agent` object (creating or clearing the `agent` key as needed).

#### Scenario: SetAgentModelAsync writes model under the agent object

- **WHEN** `SetAgentModelAsync` is called with a non-empty model
- **THEN** the model is stored under the `agent` object's `model` field

#### Scenario: SetAgentModelAsync clears the agent key when emptied

- **WHEN** `SetAgentModelAsync` is called with a null/empty model and the resulting `agent` object has no remaining keys
- **THEN** the `agent` key is cleared
- **AND** no `model` key is written or touched
