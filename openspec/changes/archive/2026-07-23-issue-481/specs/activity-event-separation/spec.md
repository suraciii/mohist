### Requirement: singular activity and event nouns resolve at the root

The root command surface SHALL expose persistent activity under the singular noun `activity` and realtime event/delivery operations under the singular noun `event`. The plural noun `events` SHALL NOT resolve and SHALL NOT be advertised as a command group.

#### Scenario: Singular nouns resolve

- **WHEN** a caller runs `mo activity list` or `mo event tail`
- **THEN** the command SHALL resolve under the singular noun

#### Scenario: Plural events noun is removed

- **WHEN** a caller runs `mo events tail` or `mo events dead-letter list`
- **THEN** the command SHALL NOT resolve
- **AND** it SHALL exit non-zero without issuing any HTTP request

#### Scenario: Root help advertises the singular nouns

- **WHEN** a caller views root command help
- **THEN** the help SHALL advertise the `activity` and `event` groups
- **AND** the help SHALL NOT advertise the plural `events` group

### Requirement: Activity evidence is reachable only through activity list

The Activity evidence collection, including its persistent recorded history and current snapshots, SHALL be reachable only through `mo activity list`. There SHALL be no `event list` (or plural `events list`) path that serves Activity evidence, so a caller cannot mistake it for a realtime event feed.

#### Scenario: No event list path serves Activity evidence

- **WHEN** a caller attempts `mo event list`
- **THEN** the command SHALL NOT serve Activity evidence
- **AND** recorded Activity history and its snapshots SHALL remain reachable only via `mo activity list`

### Requirement: the three entries carry distinct help with no shared mode flag

`activity list`, `event tail`, and `event dead-letter` SHALL each document their own read semantics in their help: bounded Activity evidence with recorded-history/snapshot provenance and Project/global scope, realtime origin from subscription establishment, and recovery side-effect respectively. The entries SHALL NOT share a `--mode` or `--source` flag that merges their behaviors.

#### Scenario: Each entry documents its own semantics

- **WHEN** a caller views the help for `mo activity list`, `mo event tail`, and `mo event dead-letter list`
- **THEN** each entry's help SHALL state its own read semantics
- **AND** the three entries SHALL NOT be merged by a single mode or source flag

#### Scenario: No mode/source flag merges the entries

- **WHEN** a caller attempts to select between persistent, realtime, and recovery behavior with a single flag
- **THEN** no such `--mode` or `--source` flag SHALL exist on these commands

### Requirement: routing remains an independent entry

Routing rule management and match evaluation SHALL remain reachable through the independent `routing` entry. `event` and `event dead-letter` commands SHALL NOT duplicate routing rule CRUD or routing match evaluation.

#### Scenario: Routing rule management stays separate

- **WHEN** a caller manages routing rules or evaluates the routing table
- **THEN** the caller SHALL use the `routing` command entry
- **AND** the `event` commands SHALL NOT expose routing rule create/edit/list or match evaluation

### Requirement: plural events is removed from navigation surfaces

The plural `events` navigation SHALL be removed from the command tree, leaf help, hints, and examples. No command, hint, or example SHALL direct a caller to use the plural `events` noun.

#### Scenario: Hints and examples do not advertise plural events

- **WHEN** a caller triggers an error hint or reads a command example that references event delivery
- **THEN** the hint or example SHALL reference the singular `event` noun
- **AND** it SHALL NOT reference the plural `events` noun
