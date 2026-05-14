## MODIFIED Requirements

### Requirement: provider-state-refresh-after-config-change

Provider configuration mutations SHALL refresh the server-side provider state after the configuration file is successfully written, so subsequent provider and model reads reflect the new configuration.

#### Scenario: Provider save refreshes provider state

- **WHEN** a client successfully creates or updates a provider through `POST /api/providers/:id`
- **THEN** server-side provider state SHALL be refreshed before the request returns success
- **AND** a subsequent `GET /api/providers` SHALL reflect the provider's updated configured state

#### Scenario: Custom provider model update refreshes model groups

- **WHEN** a client successfully updates custom provider models through `POST /api/providers/:id`
- **THEN** server-side provider state SHALL be refreshed before the request returns success
- **AND** a subsequent `GET /api/providers/models` SHALL reflect the updated custom model list

#### Scenario: Provider delete refreshes provider state

- **WHEN** a client successfully deletes a provider through `DELETE /api/providers/:id`
- **THEN** server-side provider state SHALL be refreshed before the request returns success
- **AND** subsequent provider and model reads SHALL reflect the deletion

#### Scenario: Existing provider change event remains emitted

- **WHEN** provider configuration is successfully created, updated, or deleted
- **THEN** the server SHALL continue emitting `config:providers:changed` for existing consumers

#### Scenario: Failed refresh preserves last good snapshot

- **WHEN** provider state refresh fails after a previous successful snapshot exists
- **THEN** the failed refresh SHALL NOT partially replace the last good snapshot
