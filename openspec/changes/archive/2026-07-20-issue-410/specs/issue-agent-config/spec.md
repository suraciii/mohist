### Requirement: Issue-level AgentConfig inputs accept only model and variant metadata

Issue create and update requests SHALL accept only `model`, `modelVariant`, `stageModels`, and `stageModelVariants` as model-metadata inputs. Each `stageModels`/`stageModelVariants` entry SHALL be a string-valued `provider/model-id` (first `/` separates provider from model id). The open-shape `agentConfig` field, where present, SHALL reject any ACP or liveness key — `type`, `livenessQuietThresholdMs`, `probeTimeoutMs`, `sessionStartTimeoutMs`, `compaction` — with an actionable validation error. Validation SHALL happen at the API boundary so ACP/liveness keys never reach persistence through the issue surface.

#### Scenario: A create request with only model and variant is accepted

- **WHEN** a caller creates an issue with `model: "openai/gpt-5.6-luna"` and `modelVariant: "xhigh"`
- **THEN** the API SHALL accept the request
- **AND** SHALL persist the model metadata without any ACP or liveness keys

#### Scenario: A stage model entry is validated as provider/model

- **WHEN** a caller provides `stageModels: { "plan": "zhipuai-coding-plan/glm-5.2" }`
- **THEN** the API SHALL accept the request
- **AND** SHALL reject a value that does not match the `provider/model-id` shape with an actionable validation error

#### Scenario: An ACP-type key in agentConfig is rejected

- **WHEN** a caller creates or updates an issue with an `agentConfig` containing `type: "opencode"` or `type: "openai-acp"`
- **THEN** the API SHALL reject the request with an actionable validation error
- **AND** SHALL NOT persist the `type` key

#### Scenario: A liveness key in agentConfig is rejected

- **WHEN** a caller creates or updates an issue with an `agentConfig` containing `livenessQuietThresholdMs`, `probeTimeoutMs`, or `sessionStartTimeoutMs`
- **THEN** the API SHALL reject the request with an actionable validation error
- **AND** SHALL NOT persist the liveness key

#### Scenario: A compaction key in agentConfig is rejected

- **WHEN** a caller creates or updates an issue with an `agentConfig` containing a `compaction` key
- **THEN** the API SHALL reject the request with an actionable validation error

### Requirement: The vars.agent write path emits only model and variant

The issue-level variable builder SHALL write only `model` into root `vars.agent` and only `model` and `variant` into `stages.<stage>.vars.agent`. The builder SHALL NOT emit `type`, `livenessQuietThresholdMs`, `probeTimeoutMs`, `sessionStartTimeoutMs`, `compaction`, or any other ACP-era key into either location. Project-layer and built-in workflow profile composition SHALL NOT stamp `type: "opencode"` (or any ACP/liveness key) into the project bundle, the issue bundle, or any stage's agent block.

#### Scenario: The issue variable builder writes only model into the root agent block

- **WHEN** the builder produces a model-metadata patch with a root model
- **THEN** the resulting `vars.agent` object SHALL contain at most `model`
- **AND** SHALL NOT contain `type`, `livenessQuietThresholdMs`, `probeTimeoutMs`, `sessionStartTimeoutMs`, or `compaction`

#### Scenario: The issue variable builder writes only model and variant per stage

- **WHEN** the builder produces a model-metadata patch with per-stage model and variant
- **THEN** each `stages.<stage>.vars.agent` object SHALL contain at most `model` and `variant`
- **AND** SHALL NOT contain any ACP or liveness key

#### Scenario: The project-layer writer does not stamp a type key

- **WHEN** the project-layer model-setting client writes a model or variant into the project bundle
- **THEN** the resulting `vars.agent` object SHALL contain at most `model` and `variant`
- **AND** SHALL NOT contain `type: "opencode"` or any other ACP-era key

#### Scenario: Built-in profile composition does not introduce ACP keys

- **WHEN** a built-in workflow profile composes its variables from project and issue bundles
- **THEN** the merged `vars.agent` and per-stage `vars.agent` objects SHALL NOT contain `type` or any ACP/liveness key

### Requirement: The read-back surface returns only model and variant

Issue and project read-back (issue querier, project settings reader) SHALL surface only `model` and `variant` from `vars.agent` and only `model`/`variant` per stage. The read-back SHALL NOT expose `type` or any ACP/liveness key to API clients even if such keys exist in underlying storage from prior writes. The Agent-definition `AgentConfig` read-back SHALL surface only `model` and `variant` to the web.

#### Scenario: The issue querier surfaces only model and variant

- **WHEN** the issue querier reads back an issue whose persisted `vars.agent` contains legacy `type` or liveness keys
- **THEN** the returned `agentConfig` SHALL contain at most `model` and `variant`
- **AND** the response SHALL NOT include the legacy keys

#### Scenario: The project-layer reader surfaces only model and variant

- **WHEN** the project settings reader reads back a project bundle whose `vars.agent` contains legacy keys
- **THEN** the returned model metadata SHALL contain at most `model` and `variant`
- **AND** SHALL NOT expose the legacy keys

#### Scenario: Agent-detail surfaces only model and variant

- **WHEN** the web reads an Agent definition whose stored `agentConfig` carries legacy keys
- **THEN** the surfaced Agent config SHALL contain at most `model` and `variant`
- **AND** the page SHALL NOT display an agent `type` field

### Requirement: Web and CLI surfaces stop reading and writing ACP/liveness keys

The web issue/agent/settings forms and the CLI `--agent-config` JSON option SHALL NOT read, write, or display `type` or any ACP/liveness key. The web milestone classifier SHALL NOT branch on an ACP action literal. The CLI option SHALL inherit the server-side validation result for ACP/liveness keys and SHALL NOT perform additional client-side key filtering beyond the converged shape.

#### Scenario: The web issue form sends only model and variant

- **WHEN** a user submits an issue create or update from the web
- **THEN** the request body SHALL contain at most `model`, `modelVariant`, `stageModels`, and `stageModelVariants`
- **AND** SHALL NOT contain `type` or any ACP/liveness key in `agentConfig`

#### Scenario: The agent profile editor writes only model and variant

- **WHEN** a user creates or updates an Agent definition from the web
- **THEN** the persisted `agentConfig` SHALL contain at most `model` and `variant`
- **AND** the editor SHALL NOT preserve legacy ACP/liveness keys via spread or merge

#### Scenario: The CLI rejects an agent-config payload with ACP keys

- **WHEN** a caller invokes `mo agent create` or `mo agent update` with `--agent-config` JSON containing `type` or a liveness key
- **THEN** the CLI SHALL rely on the server-side validation result
- **AND** the server SHALL reject the request at the API boundary

### Requirement: Already-persisted legacy keys are tolerated, not migrated

A `vars.agent` object already in storage that contains legacy ACP/liveness keys from a prior write SHALL NOT be rewritten or migrated by this change. The `mohist/opencode` runtime SHALL ignore unknown `options` keys and record them as diagnostics when such keys reach an execution request. The issue/agent/profile storage integrity check SHALL NOT mutate or strip legacy keys during defensive-copy operations.

#### Scenario: Legacy keys in storage are not rewritten

- **WHEN** the issue/agent/profile storage integrity check encounters a `vars.agent` with legacy keys
- **THEN** it SHALL NOT strip, mutate, or rewrite those keys
- **AND** the persisted bundle SHALL remain byte-equivalent for the legacy portions

#### Scenario: The OpenCode runtime records unknown keys as diagnostics

- **WHEN** a `vars.agent` carrying legacy keys is bound into an `options` payload and reaches the `mohist/opencode` runtime
- **THEN** the runtime SHALL ignore the unknown keys
- **AND** SHALL record them as diagnostics rather than failures

#### Scenario: An Agent-definition with legacy keys continues to launch

- **WHEN** an Agent definition whose stored `agentConfig` contains legacy keys is launched
- **THEN** the launch SHALL succeed using the model/variant portion of the snapshot
- **AND** the legacy keys SHALL NOT be passed through to the runtime as execution parameters
