## ADDED Requirements

### Requirement: spawn_agent tool truncates subprocess stdout
The spawn_agent tool SHALL truncate subprocess stdout when it exceeds 8000 characters, preserving the first 3000 and last 5000 characters with a truncation notice in between.

#### Scenario: Stdout within limit
- **WHEN** opencode subprocess returns stdout of 5000 characters
- **THEN** the full stdout SHALL be returned without truncation

#### Scenario: Stdout exceeds limit
- **WHEN** opencode subprocess returns stdout of 20000 characters
- **THEN** the result SHALL contain the first 3000 characters
- **AND** a truncation notice SHALL be inserted
- **AND** the last 5000 characters SHALL be included

#### Scenario: Stdout exactly at limit
- **WHEN** opencode subprocess returns stdout of exactly 8000 characters
- **THEN** the full stdout SHALL be returned without truncation

### Requirement: LLM config is loaded from config table and passed to resolveModel
The system SHALL read LLM configuration from the config table (keys: `llm.model`, `llm.provider.<id>.options.baseURL`) and pass it to `resolveModel()` so that user-configured model and proxy settings take effect.

#### Scenario: LLM model configured in config table
- **WHEN** `llm.model` is set to "anthropic/claude-sonnet-4-20250514" in config table
- **THEN** `resolveModel()` SHALL use that model instead of the hardcoded default

#### Scenario: LLM proxy configured in config table
- **WHEN** `llm.provider.anthropic.options.baseURL` is set in config table
- **THEN** `resolveModel()` SHALL create the provider with that baseURL

#### Scenario: No LLM config in config table
- **WHEN** no `llm.model` key exists in config table
- **THEN** `resolveModel()` SHALL use the default model (`anthropic/claude-sonnet-4-20250514`)
- **AND** SHALL detect API key from environment variables
