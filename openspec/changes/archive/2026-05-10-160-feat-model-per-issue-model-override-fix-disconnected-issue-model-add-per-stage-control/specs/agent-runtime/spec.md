## ADDED Requirements

### Requirement: Model discovery does not create opencode sessions

Model discovery SHALL list available opencode models without creating ACP sessions or persistent opencode session records. Discovery SHALL return model identifiers in `provider/model` format and cache successful results for 30 minutes.

#### Scenario: Discover models through lightweight CLI

- **WHEN** available opencode models are requested
- **THEN** Mohist runs the lightweight `opencode models` command
- **AND** parses returned `provider/model` identifiers
- **AND** does not call ACP `newSession()`

#### Scenario: Discovery cache is fresh for 30 minutes

- **WHEN** model discovery succeeds
- **THEN** subsequent requests within 30 minutes return the cached model list
- **AND** do not spawn another discovery process

#### Scenario: Discovery command fails

- **WHEN** `opencode models` fails or returns no parseable model list
- **THEN** the discovery service reports an error to callers
- **AND** logs the failure for diagnosis
