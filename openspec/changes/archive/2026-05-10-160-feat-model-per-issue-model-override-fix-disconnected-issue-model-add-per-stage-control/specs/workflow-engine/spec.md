## ADDED Requirements

### Requirement: Workflow uses issue-aware model resolution

The workflow engine and issue-bound recovery sessions SHALL resolve coder models with issue-level overrides before global configuration. The fallback order SHALL be `issue.stageModels[stage]`, then `issue.model`, then `config.opencode.stageModels[stage]`, then `config.opencode.model`, then opencode default.

#### Scenario: Issue stage model overrides all lower levels

- **WHEN** an issue has `stageModels.build = "anthropic/claude-opus-4-20250514"`
- **AND** the issue has `model = "anthropic/claude-sonnet-4-20250514"`
- **AND** global build/default models are configured
- **THEN** the build-stage coder session uses `"anthropic/claude-opus-4-20250514"`

#### Scenario: Issue default model applies when stage override is unset

- **WHEN** an issue has `model = "openai/gpt-4o"`
- **AND** no issue stage model exists for the current stage
- **THEN** the coder session uses `"openai/gpt-4o"`
- **AND** global stage/default models are ignored

#### Scenario: Global configuration remains fallback

- **WHEN** an issue has no issue-level model metadata
- **AND** global stage or default model configuration exists
- **THEN** the coder session uses the existing global model resolution behavior

#### Scenario: Recovery sessions use build-stage policy

- **WHEN** conflict resolution or build-error-fix starts an issue-bound coder session
- **THEN** the session resolves its model using build-stage policy plus the issue-level overrides
