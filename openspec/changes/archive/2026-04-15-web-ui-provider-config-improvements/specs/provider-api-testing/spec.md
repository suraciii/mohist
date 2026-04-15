## ADDED Requirements

### Requirement: Provider API endpoints testing
The system SHALL provide comprehensive tests for all Provider API endpoints including GET, POST, DELETE, and POST /test endpoints.

#### Scenario: GET /api/providers returns provider list
- **WHEN** a client sends GET request to /api/providers
- **THEN** the system SHALL return a list of providers with correct structure including id, name, baseURL, models, configured, source, isBuiltin, isDefault, and apiKeyMasked fields

#### Scenario: POST /api/providers/:id creates new provider
- **WHEN** a client sends POST request to /api/providers/:id with valid provider data
- **THEN** the system SHALL save the provider configuration and return success response

#### Scenario: POST /api/providers/:id validates required fields
- **WHEN** a client sends POST request without required apiKey field
- **THEN** the system SHALL return 400 Bad Request with validation error message

#### Scenario: DELETE /api/providers/:id removes provider
- **WHEN** a client sends DELETE request to /api/providers/:id for existing provider
- **THEN** the system SHALL remove the provider configuration and return success response

#### Scenario: POST /api/providers/test validates connection
- **WHEN** a client sends POST request to /api/providers/test with valid credentials
- **THEN** the system SHALL attempt to connect to the provider and return success/failure result

### Requirement: Provider API rate limiting tests
The system SHALL test that the rate limiting functionality correctly limits requests to the test endpoint.

#### Scenario: Rate limit allows requests within limit
- **WHEN** 30 requests are sent within 1 minute from the same IP
- **THEN** all requests SHALL be processed successfully

#### Scenario: Rate limit blocks excessive requests
- **WHEN** 31 requests are sent within 1 minute from the same IP
- **THEN** the 31st request SHALL receive 429 Too Many Requests response with Retry-After header

#### Scenario: Rate limit resets after window
- **GIVEN** the rate limit has been reached
- **WHEN** the rate limit window expires (1 minute)
- **THEN** new requests SHALL be allowed again

### Requirement: Config hot reload tests
The system SHALL test that configuration changes trigger proper hot reload behavior.

#### Scenario: Config change emits event
- **WHEN** a provider configuration is saved via POST /api/providers/:id
- **THEN** the system SHALL emit 'config:providers:changed' event via EventBus

#### Scenario: Config deletion emits event
- **WHEN** a provider configuration is deleted via DELETE /api/providers/:id
- **THEN** the system SHALL emit 'config:providers:changed' event via EventBus

#### Scenario: AgentRunner receives hot reload notification
- **GIVEN** AgentRunnerService is initialized and listening to events
- **WHEN** a 'config:providers:changed' event is emitted
- **THEN** the AgentRunnerService SHALL reload its LLM configuration
