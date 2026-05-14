## MODIFIED Requirements

### Requirement: provider-api-cached-reads

The Provider API SHALL serve provider list and provider model reads from server-side in-memory provider state that is prewarmed before the HTTP server accepts requests.

#### Scenario: Provider state prewarmed before serving requests

- **WHEN** the server starts successfully
- **THEN** provider state has already built provider list and provider model-group snapshots
- **AND** the provider read endpoints can return data without performing full provider/model aggregation on the first request

#### Scenario: Provider list omits model IDs

- **WHEN** a client requests `GET /api/providers`
- **THEN** each provider item includes provider metadata such as `id`, `name`, `baseURL`, `configured`, `source`, `isBuiltin`, `isDefault`, and `apiKeyMasked`
- **AND** provider items SHALL NOT include a `models` field

#### Scenario: Provider models preserve selectable model response shape

- **WHEN** a client requests `GET /api/providers/models`
- **THEN** the response contains provider groups with `id`, `name`, `configured`, and `models`
- **AND** each model item contains `id`, `name`, `badges`, and `contextWindow`
- **AND** the response is read from provider state rather than rebuilt independently in the route handler

### Requirement: provider-list-omits-models

The web client SHALL treat `GET /api/providers` as a lightweight provider metadata endpoint and SHALL use `GET /api/providers/models` for selectable model data.

#### Scenario: Provider list UI consumes lightweight providers

- **WHEN** the AI settings provider list renders
- **THEN** it SHALL NOT read model IDs from provider items returned by `GET /api/providers`
- **AND** it SHALL continue to render provider connection status, source, default status, and masked API key state

#### Scenario: Model selectors consume model groups endpoint

- **WHEN** the AI settings model selectors render
- **THEN** they SHALL load selectable models from `GET /api/providers/models`
- **AND** model selection behavior SHALL remain unchanged

### Requirement: provider-api-performance-contract

Provider API changes SHALL be covered by regression tests that protect the lightweight response contract and cache refresh behavior.

#### Scenario: Lightweight provider response is tested

- **WHEN** provider API tests request `GET /api/providers`
- **THEN** tests verify provider items do not include `models`

#### Scenario: Cached provider model response is tested

- **WHEN** provider API tests request `GET /api/providers/models`
- **THEN** tests verify model groups preserve the expected response shape

#### Scenario: Cached state refresh is tested

- **WHEN** provider API tests mutate provider configuration
- **THEN** tests verify subsequent provider reads reflect the refreshed provider state
