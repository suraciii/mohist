### Requirement: One four-state executability projection

The Server SHALL expose exactly `not-configured`, `not-executable`, `unknown`,
and `executable` as an Agent's executability state. Structural gaps yield
`not-configured`; a matching execution-configuration failure yields
`not-executable`; matching successful evidence yields `executable`; and all
other structurally complete definitions yield `unknown`.

Each blocked state SHALL carry every gap's message, next action, and fix entry
point. `unknown` SHALL explain that a launch is accepted and awaits Runner
verification. The result SHALL be re-derived on definition reads rather than
persisted.

#### Scenario: A definition is incomplete

- **WHEN** instructions, a valid model reference, or a supported runtime is
  missing
- **THEN** the Agent's executability is `not-configured`
- **AND** every structural defect has an actionable gap

#### Scenario: Matching runtime evidence rejects configuration

- **WHEN** the latest execution matching the current definition failed due to
  provider credentials, model usability, or runtime configuration
- **THEN** the Agent's executability is `not-executable`
- **AND** it is not presented as a missing-definition gap

### Requirement: One admission boundary

All Agent launch entry points SHALL consume the Server executability projection.
`not-configured` and `not-executable` SHALL reject before creating a Job or
Session with distinct error codes. `unknown` and `executable` SHALL remain
admissible. No launch entry point may derive an independent verdict from raw
Agent configuration.

### Requirement: Executability and availability are separate

Web and CLI Agent surfaces SHALL render the Server executability projection
and the transient Availability projection as separately labeled signals. A
client SHALL not synthesize either state from the other.
