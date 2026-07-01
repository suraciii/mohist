### Requirement: CLI reference documents every real command group

`docs/cli-reference.md` is the canonical `mo` CLI command reference and SHALL document every real command group the CLI provides, including (without limitation) the `mo agent`, `mo label`, `mo workflow`, and `mo otel` command groups that were previously omitted. Each documented command group SHALL describe its subcommands and their purpose consistent with the CLI's actual behavior.

#### Scenario: Previously omitted command groups are documented

- **WHEN** a reader consults `docs/cli-reference.md`
- **THEN** it SHALL contain documented sections for the `agent`, `label`, `workflow`, and `otel` command groups

#### Scenario: Documented groups match the real CLI surface

- **WHEN** a command group is documented in `docs/cli-reference.md`
- **THEN** that group SHALL exist in the real `mo` CLI
- **AND** its documented subcommands SHALL match the CLI's actual subcommands

### Requirement: CLI reference does not claim Web UI parity or false completeness

`docs/cli-reference.md` SHALL NOT claim parity with, equivalence to, or feature-equality against the Web UI, and SHALL NOT market itself as a complete command reference while omitting command groups. The reference SHALL describe the CLI's own scope without asserting it replicates the Web UI.

#### Scenario: No equivalence claim remains

- **WHEN** a reader consults `docs/cli-reference.md`
- **THEN** it SHALL NOT contain any statement claiming the CLI is equivalent to, or functionally equal to, the Web UI

#### Scenario: Completeness claim is not made while groups are missing

- **WHEN** `docs/cli-reference.md` describes its coverage
- **THEN** it SHALL NOT assert it is a complete reference unless every real command group is documented
