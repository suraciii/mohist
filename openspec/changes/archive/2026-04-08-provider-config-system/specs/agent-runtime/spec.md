## MODIFIED Requirements

### Requirement: LLM provider configuration
The system SHALL support configuring LLM providers via ConfigLoader (from `~/.mohist/config.jsonc` merged with environment variables). The configuration SHALL include: default model in "provider/model-id" format, and per-provider options (baseURL, apiKey). API keys SHALL be resolved from config file first, falling back to environment variables. The system SHALL support any provider defined in the builtin registry or user config, not just anthropic/openai.

#### Scenario: Load provider config from config file
- **WHEN** Mohist server starts
- **THEN** the system SHALL load config from `~/.mohist/config.jsonc` via ConfigLoader
- **AND** resolve API key from config file first, then environment variables
- **AND** the configured model SHALL be used for LLM calls

#### Scenario: Config with custom provider
- **WHEN** config.jsonc defines `provider.glm` with apiKey and model is "glm/glm-4-plus"
- **THEN** the system SHALL use the builtin glm provider's baseURL
- **AND** create an openai-compatible SDK instance with that apiKey and baseURL

#### Scenario: Config with proxy
- **WHEN** config.jsonc defines `provider.anthropic.options.baseURL`
- **THEN** the system SHALL use that baseURL for the provider's API calls

#### Scenario: Fallback to environment variables
- **WHEN** config.jsonc does not define provider.anthropic
- **AND** ANTHROPIC_API_KEY environment variable is set
- **THEN** the system SHALL use the environment variable as API key

## REMOVED Requirements

### Requirement: LLM config is loaded from config table and passed to resolveModel
**Reason**: Provider configuration migrated from SQLite ConfigRepo to file-based ConfigLoader
**Migration**: Users should use `~/.mohist/config.jsonc` instead of `mo config set llm.model` / `mo config set llm.provider.*`
