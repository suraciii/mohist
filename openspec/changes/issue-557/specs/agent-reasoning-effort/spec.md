### Requirement: Agent configuration has a canonical reasoning-effort field

An Agent execution configuration SHALL represent `reasoningEffort` as a first-class field separate from `runtime`, `model`, and `variant`. The canonical vocabulary SHALL be stable, ordered from no reasoning through the highest effort, and shared by persistence, API, Web, CLI, readiness, and launch-result surfaces. The explicit no-reasoning value SHALL be distinct from an unset value.

#### Scenario: A valid effort and runtime variant are stored independently

- **WHEN** a user creates or edits an Agent with a valid `reasoningEffort`, a model, and a runtime-specific `variant`
- **THEN** the Agent configuration SHALL persist the effort and variant as separate fields
- **AND** reading the Agent SHALL return the same canonical effort value and the same variant value without translating one into the other

#### Scenario: The explicit no-reasoning value is distinct from an unset value

- **WHEN** one Agent is configured with the canonical no-reasoning value and another Agent omits or clears `reasoningEffort`
- **THEN** the first Agent SHALL report an explicit no-reasoning choice
- **AND** the second Agent SHALL report reasoning effort as unset
- **AND** neither state SHALL be silently converted into the other

### Requirement: Agent configuration validates effort values without coercion

Agent create and edit surfaces SHALL accept only canonical reasoning-effort values or an explicit clear operation. An empty string, an unknown token, or a non-string value SHALL produce an actionable field-level validation result and SHALL NOT be coerced into a default effort or a runtime variant. If invalid legacy or externally persisted configuration reaches readiness, the system SHALL preserve the distinction and report it as an invalid or unknown effort rather than executing it.

#### Scenario: An unknown effort is rejected at the write boundary

- **WHEN** a caller submits `reasoningEffort` with an unknown token or a blank string
- **THEN** the Agent create or edit operation SHALL reject the configuration with an actionable reasoning-effort validation error
- **AND** it SHALL NOT store the value as a variant or replace it with a default

#### Scenario: A persisted invalid effort is reported before launch

- **WHEN** an Agent contains a non-canonical reasoning-effort value that was not rejected at the write boundary
- **THEN** readiness SHALL report an explicit invalid or unknown reasoning-effort gap
- **AND** a launch SHALL not dispatch an execution using that value

### Requirement: Clearing reasoning effort is observable

The Agent API, Web editor, and CLI SHALL support setting and clearing `reasoningEffort`. A clear operation SHALL remove or null the field according to the Agent configuration contract, and list/detail/read-back surfaces SHALL display the resulting unset state. Clearing the field SHALL NOT silently select a runtime default or reinterpret the current `variant` as the effort.

#### Scenario: A user clears the configured effort

- **WHEN** a user clears `reasoningEffort` from an Agent
- **THEN** the persisted Agent configuration SHALL contain the defined unset representation
- **AND** the Agent list and detail surfaces SHALL show the effort as unset
- **AND** the existing runtime-specific variant SHALL remain unchanged as a separate field

### Requirement: Agent surfaces expose the complete execution configuration

Agent list, Agent detail, Agent Connection readiness/launch, readiness, launch response, and launch observation surfaces SHALL use one stable projection containing `runtime`, `model`, `reasoningEffort`, and `variant`. The projection SHALL show the final configured values, label an unset or invalid effort explicitly, and SHALL never display a runtime-specific variant as the Agent's reasoning effort.

#### Scenario: List, detail, and launch result agree

- **WHEN** an Agent is configured with runtime `opencode`, model `openai/gpt-5.6`, a valid reasoning effort, and a runtime-specific variant
- **THEN** the Agent list, detail response, readiness response, and accepted launch result SHALL expose all four fields
- **AND** the reasoning-effort and variant fields SHALL retain their distinct values and names

#### Scenario: Connection launch agrees with direct launch

- **WHEN** the same configured Agent is launched directly and through a bound Agent Connection
- **THEN** the Agent Connection readiness and accepted launch result SHALL expose the same runtime, model, reasoning effort, and variant projection as the direct launch
- **AND** clearing or changing effort SHALL not reinterpret the Connection's runtime-specific variant
