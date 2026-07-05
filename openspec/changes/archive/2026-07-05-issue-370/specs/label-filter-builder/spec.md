### Requirement: Label-filter dictionary construction has a single authoritative implementation

The label-filter dictionary builder — which converts a variable list of `(key, value)` pairs into an ordinal-compared dictionary, skipping any pair whose key or value is null or whitespace — SHALL exist as exactly one authoritative implementation in the codebase. The duplicate copy that currently lives on the activity feed assembler (byte-for-byte identical to the querier's) SHALL be removed, and the usage reporter SHALL call the authoritative implementation instead of routing through the querier. The core query class (`AgentSessionQuerier`) SHALL NOT expose `Labels` as an `internal static` member after this change.

#### Scenario: Whitespace or null keys are skipped

- **WHEN** the builder is invoked with a pair whose key is null, empty, or whitespace
- **THEN** that pair SHALL NOT appear in the resulting dictionary, regardless of its value

#### Scenario: Whitespace or null values are skipped

- **WHEN** the builder is invoked with a pair whose value is null, empty, or whitespace
- **THEN** that pair SHALL NOT appear in the resulting dictionary, regardless of its key

#### Scenario: Valid pairs populate an ordinal dictionary

- **WHEN** the builder is invoked with pairs whose keys and values are all non-whitespace
- **THEN** the resulting dictionary SHALL contain each pair and SHALL use ordinal (case-sensitive) key comparison

#### Scenario: All three consumers call the single implementation

- **WHEN** the codebase is inspected after the change
- **THEN** the core query service, the activity feed assembler, and the usage reporter SHALL all obtain label-filter dictionaries from the single authoritative implementation, and the activity feed assembler SHALL NOT declare its own local `Labels` builder

#### Scenario: Core query class no longer carries the static label builder

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT declare a static `Labels(params (string, string?)[])` member
