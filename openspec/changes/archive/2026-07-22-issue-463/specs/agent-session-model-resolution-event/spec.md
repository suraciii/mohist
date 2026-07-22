### Requirement: Unified model-resolution event across runtimes

Every runtime SHALL emit model resolution as the `model.resolved` event type carrying the `resolvedModel` field (the resolved model identifier in the runtime's canonical form, e.g. `<providerId>/<modelId>`). No runtime SHALL carry the resolved model only under a different event type or field name. The OpenCode runtime already conforms; the Pi runtime SHALL conform.

#### Scenario: Pi runtime model change emits model.resolved

- **WHEN** the Pi runtime signals a model change
- **THEN** the runner SHALL emit a `model.resolved` event whose payload contains `resolvedModel`

#### Scenario: OpenCode runtime model resolution is unchanged

- **WHEN** the OpenCode runtime resolves a model
- **THEN** the runner SHALL emit a `model.resolved` event carrying `resolvedModel` as it does today

#### Scenario: No resolved-model-only alternate shape

- **WHEN** any runtime resolves a model
- **THEN** the runner SHALL NOT carry that resolved model solely under a `status` event with a `model` field; the model resolution SHALL be expressible as `model.resolved` with `resolvedModel`

### Requirement: Web model.resolved event carries resolvedModel

The web's `model.resolved` live-event contract SHALL read the resolved model from the `resolvedModel` field, matching the field the runtimes emit and the rest of the web reads. The web SHALL NOT declare or read a `model` field for the `model.resolved` event.

#### Scenario: Web model.resolved event type uses resolvedModel

- **WHEN** the web defines its `model.resolved` live-event contract
- **THEN** the resolved model SHALL be carried in the `resolvedModel` field, not `model`

### Requirement: Server reads the resolved model from one consistent field

The server SHALL read the resolved model from the same payload field of the `model.resolved` event in both live-state application and transcript-summary projection, so the live session state and the transcript summary agree on the resolved model.

#### Scenario: A model.resolved event updates both live state and summary

- **WHEN** the server ingests a `model.resolved` event carrying `resolvedModel`
- **THEN** both the session's resolved model and the transcript summary's resolved model SHALL reflect that value

#### Scenario: Live state and summary do not diverge

- **WHEN** the server ingests a model-resolution event
- **THEN** the resolved model value applied to the live session state SHALL equal the resolved model value projected into the transcript summary

### Requirement: Resolved model name is visible for every runtime

The resolved model name SHALL be visible in the web read model for a session regardless of which runtime the session runs under.

#### Scenario: Resolved model is shown for a Pi session

- **WHEN** a session runs under the Pi runtime and resolves a model
- **THEN** the web SHALL display the resolved model name

#### Scenario: Resolved model is shown for an OpenCode session

- **WHEN** a session runs under the OpenCode runtime and resolves a model
- **THEN** the web SHALL display the resolved model name
