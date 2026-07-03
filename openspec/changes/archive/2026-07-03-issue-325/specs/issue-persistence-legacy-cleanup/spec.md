### Requirement: No legacy label normalization

Issue deserialization MUST NOT carry any deprecated label-format normalization path. The persistence layer SHALL accept only the current label object format and MUST NOT inspect, strip, or rewrite labels based on a legacy array format.

#### Scenario: Current label object format deserializes unchanged

- **WHEN** an issue is deserialized from persisted state carrying the current label object format
- **THEN** the labels are populated exactly as before this change, with no normalization branch executing

#### Scenario: Legacy array-format labels are no longer normalized

- **WHEN** an issue is deserialized from persisted state carrying the legacy array-format labels
- **THEN** the persistence layer no longer strips and replaces them with an empty object; no legacy normalization code path exists

### Requirement: No legacyLabelsDiscarded surfacing

The `legacyLabelsDiscarded` deserialization overload and any surfacing of a "labels were discarded" signal MUST be removed. Deserialization SHALL expose only the single-result form.

#### Scenario: Deserialization exposes only the single-result overload

- **WHEN** code calls `IssueStore.Deserialize`
- **THEN** only the overload returning the deserialized issue (with no `out bool legacyLabelsDiscarded` parameter) is available
