## ADDED Requirements

### Requirement: Built-in models registry
The system SHALL maintain a registry of built-in models for all supported providers.

#### Scenario: Registry contains model metadata
- **WHEN** the system queries the built-in models registry
- **THEN** it returns model metadata including: ID, display name, provider, badges, context window size

#### Scenario: Registry supports all built-in providers
- **WHEN** the registry is queried
- **THEN** it includes models for: anthropic, openai, glm, kimi, minimax, deepseek, qwen

### Requirement: Model metadata structure
Each model in the registry SHALL have complete metadata.

#### Scenario: Model metadata fields
- **WHEN** accessing a model's metadata
- **THEN** the following fields are available:
  - `id`: unique model identifier (e.g., "claude-sonnet-4-20250514")
  - `name`: human-readable display name (e.g., "Claude Sonnet 4")
  - `provider`: provider ID (e.g., "anthropic")
  - `description`: optional model description
  - `badges`: array of badges like "free", "latest"
  - `contextWindow`: context window size in tokens
  - `variants`: optional array of variant configurations

### Requirement: Provider model availability
The system SHALL determine which models are available based on provider configuration.

#### Scenario: Filter by configured providers
- **WHEN** a provider is not configured (no API key)
- **THEN** its models are not included in the available models list
- **AND** the provider is shown in the UI with a "Connect" prompt

#### Scenario: Include custom provider models
- **WHEN** a custom provider is configured with a models list
- **THEN** those models are included in the available models list
- **AND** they are grouped under the custom provider

### Requirement: API endpoint for models
The system SHALL expose an API endpoint to retrieve available models.

#### Scenario: GET /api/models
- **WHEN** the frontend requests GET /api/models
- **THEN** the system returns a list of all available models
- **AND** each model includes complete metadata
- **AND** models are grouped by provider

#### Scenario: Models response structure
- **WHEN** the API returns models
- **THEN** the response includes:
  - `providers`: array of provider groups
  - Each provider contains `id`, `name`, `configured`, `models[]`
  - Each model contains `id`, `name`, `badges`, `contextWindow`
