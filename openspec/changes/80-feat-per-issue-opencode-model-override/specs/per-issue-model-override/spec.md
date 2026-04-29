## ADDED Requirements

### Requirement: Per-issue model override storage

The system SHALL store a per-issue model preference in the `issues` table as a nullable `model TEXT` column. `NULL` means no override (fallback to stageModels/global). The value SHALL be in `"provider/model-id"` format (e.g. `"anthropic/claude-sonnet-4-20250514"`).

#### Scenario: Issue with model override

- **WHEN** an issue has `model` set to `"anthropic/claude-sonnet-4-20250514"`
- **THEN** `findById` and `findAll` return the issue with `model: "anthropic/claude-sonnet-4-20250514"`

#### Scenario: Issue without model override

- **WHEN** an issue has `model` set to `NULL`
- **THEN** `findById` and `findAll` return the issue with `model: undefined`

#### Scenario: Clear model override

- **WHEN** `updateModel(id, null)` is called
- **THEN** the issue's `model` column is set to `NULL`
- **AND** subsequent queries return `model: undefined`

#### Scenario: Set model override

- **WHEN** `updateModel(id, "openai/gpt-4o")` is called
- **THEN** the issue's `model` column is set to `"openai/gpt-4o"`
- **AND** `updated_at` is refreshed

### Requirement: Per-issue model is highest priority in model resolution

The system SHALL use the following priority chain when resolving the coder model for an ACP session:

1. `issue.model` (per-issue, highest priority)
2. `opencode.stageModels[stage]` (per-stage, from Issue #74)
3. `opencode.model` (global default, from Issue #74)
4. opencode built-in default

When a higher-priority source is set, lower-priority sources SHALL be ignored.

#### Scenario: Issue model overrides stage model

- **WHEN** issue has `model = "openai/gpt-4o"`
- **AND** `opencode.stageModels.build = "anthropic/claude-sonnet-4-20250514"`
- **THEN** the ACP session uses `"openai/gpt-4o"`

#### Scenario: No issue model, stage model applies

- **WHEN** issue has `model = NULL`
- **AND** `opencode.stageModels.build = "anthropic/claude-sonnet-4-20250514"`
- **THEN** the ACP session uses `"anthropic/claude-sonnet-4-20250514"`

#### Scenario: No issue model, no stage model, global applies

- **WHEN** issue has `model = NULL`
- **AND** no `opencode.stageModels` is configured for the current stage
- **AND** `opencode.model = "anthropic/claude-sonnet-4-20250514"`
- **THEN** the ACP session uses `"anthropic/claude-sonnet-4-20250514"`

### Requirement: Model override takes effect on next ACP session

Setting a model override SHALL NOT affect currently running ACP sessions. The override SHALL take effect on the next ACP session (next plan round, build task, or review round).

#### Scenario: Model change during running session

- **WHEN** an ACP session is running for issue #5
- **AND** user sets `model = "openai/gpt-4o"` via PATCH API
- **THEN** the currently running session continues with its original model
- **AND** the next ACP session for issue #5 uses `"openai/gpt-4o"`

### Requirement: Model is passed through the call chain without DB coupling

The `AcpSessionOptions` and `AcpConnectionOptions` interfaces SHALL accept an optional `model?: string` field. The workflow controller and ralph executor SHALL read `issue.model` from the issue object and pass it through to the ACP session runner. The ACP session module SHALL NOT query the issues table directly.

#### Scenario: Workflow controller passes issue model

- **WHEN** workflow controller starts a plan or review stage for an issue with `model = "openai/gpt-4o"`
- **THEN** the `AcpConnectionOptions` includes `model: "openai/gpt-4o"`

#### Scenario: Ralph executor passes issue model

- **WHEN** ralph executor runs a build task for an issue with `model = "openai/gpt-4o"`
- **THEN** the `_acpSessionRunner` call includes `model: "openai/gpt-4o"` in its options

#### Scenario: Explore sessions are not affected

- **WHEN** an explore session is started
- **THEN** the per-issue model override is NOT applied
- **AND** explore uses its own model selection mechanism
