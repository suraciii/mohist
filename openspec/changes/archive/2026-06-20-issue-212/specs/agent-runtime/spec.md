## MODIFIED Requirements

### Requirement: Model discovery does not create opencode sessions

Model discovery SHALL list available opencode models without creating ACP sessions or persistent opencode session records. Discovery SHALL return, for each model, its model identifier in `provider/model` format together with that model's supported reasoning variant set (which MAY be empty), and SHALL cache successful results for 30 minutes. A model that reports no variants SHALL be represented with an empty variant set.

#### Scenario: Discover models through lightweight CLI

- **WHEN** available opencode models are requested
- **THEN** Mohist runs the lightweight `opencode models` command
- **AND** parses returned `provider/model` identifiers together with each model's supported reasoning variant set
- **AND** does not call ACP `newSession()`

#### Scenario: Discovery returns per-model variant sets

- **WHEN** a model reports that it supports one or more reasoning variants
- **THEN** discovery SHALL return those variants alongside the model identifier
- **AND** a model that reports no variants SHALL be returned with an empty variant set

#### Scenario: Discovery cache is fresh for 30 minutes

- **WHEN** model discovery succeeds
- **THEN** subsequent requests within 30 minutes return the cached model list and variant sets
- **AND** do not spawn another discovery process

#### Scenario: Discovery command fails

- **WHEN** `opencode models` fails or returns no parseable model list
- **THEN** the discovery service reports an error to callers
- **AND** logs the failure for diagnosis

## ADDED Requirements

### Requirement: Runner applies reasoning variant to coder session on a best-effort basis

The runner SHALL receive the reasoning variant selected for a model as part of coder work dispatch and SHALL apply it to the opencode coder session before prompt execution. The runner SHALL NOT pre-validate the variant against discovery before delivery. If the model ignores or rejects the variant, the runner SHALL keep the session running and SHALL NOT fail the work solely because of the variant. When no variant is present in dispatch, the runner SHALL launch the session with the same behavior as before this capability existed.

#### Scenario: Supported variant applied before prompt execution

- **WHEN** coder work dispatch carries a variant the model reports as supported
- **THEN** the runner SHALL apply the variant to the coder session before prompt execution
- **AND** the session SHALL run with the selected reasoning effort

#### Scenario: Unsupported variant does not fail the work

- **WHEN** coder work dispatch carries a variant the model does not support
- **AND** the model ignores or rejects the variant
- **THEN** the runner SHALL keep the session running
- **AND** SHALL NOT fail the work solely because of the variant

#### Scenario: No variant in dispatch behaves as before

- **WHEN** coder work dispatch carries no variant for the model
- **THEN** the runner SHALL launch the coder session with the same behavior as before this capability existed
- **AND** the runner SHALL NOT treat the missing variant as an error
