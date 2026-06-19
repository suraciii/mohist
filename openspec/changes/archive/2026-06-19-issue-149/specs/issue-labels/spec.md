## ADDED Requirements

### Requirement: Issue labels are key-value pairs with single value per key

An Issue label SHALL be a `{key, value}` pair. The label set on an Issue SHALL be a map in which each key maps to at most one value. Labels SHALL NOT be multi-valued (a key cannot hold more than one value), and there SHALL be no labels without a key.

#### Scenario: Labels are stored as a key-value map
- **WHEN** an Issue has labels `stream:frontend` and `module:auth`
- **THEN** the Issue's labels are represented as the map `{ "stream": "frontend", "module": "auth" }`
- **AND** each key appears at most once

#### Scenario: Setting a value for an existing key replaces the prior value
- **WHEN** an Issue has label `stream:frontend` and the value `backend` is set for the same key `stream`
- **THEN** the Issue's labels map contains `{ "stream": "backend" }`
- **AND** the prior value `frontend` for key `stream` is no longer present

### Requirement: Label keys are validated

A label key SHALL match the pattern `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$` (lowercase ASCII alphanumeric characters and interior dashes). A key SHALL NOT be empty, contain uppercase characters, whitespace, or leading/trailing dashes. An invalid key SHALL be rejected with a clear validation error and SHALL NOT be persisted.

#### Scenario: Valid key is accepted
- **WHEN** a label is set with key `stream`, `module-auth`, `stream--auth`, or `a1`
- **THEN** the key is accepted and the label is persisted

#### Scenario: Invalid key is rejected
- **WHEN** a label is set with key `Stream` (uppercase), `stream frontend` (whitespace), `-stream` (leading dash), `stream-` (trailing dash), or empty
- **THEN** the operation is rejected with a clear error
- **AND** the label is not persisted

### Requirement: Label values are non-empty

A label value SHALL be a non-empty, non-whitespace string. An empty or whitespace-only value SHALL be rejected with a clear validation error and SHALL NOT be persisted.

#### Scenario: Non-empty value is accepted
- **WHEN** a label is set with value `frontend`
- **THEN** the value is accepted and the label is persisted

#### Scenario: Empty value is rejected
- **WHEN** a label is set with an empty value or a whitespace-only value
- **THEN** the operation is rejected with a clear error
- **AND** the label is not persisted

### Requirement: Label operations are key-addressed

Label mutations SHALL be addressed by key. `SetLabel(key, value)` SHALL upsert the key to the given value. `RemoveLabel(key)` SHALL remove the key and SHALL be idempotent (removing a missing key is not an error). A full replacement SHALL replace the entire label map.

#### Scenario: SetLabel adds a new key
- **WHEN** `SetLabel("stream", "frontend")` is applied to an Issue with no `stream` label
- **THEN** the Issue's labels map contains `{ "stream": "frontend" }`

#### Scenario: SetLabel updates an existing key's value
- **WHEN** `SetLabel("stream", "backend")` is applied to an Issue whose labels map contains `{ "stream": "frontend" }`
- **THEN** the Issue's labels map becomes `{ "stream": "backend" }`

#### Scenario: RemoveLabel deletes an existing key
- **WHEN** `RemoveLabel("stream")` is applied to an Issue whose labels map contains `{ "stream": "frontend" }`
- **THEN** the Issue's labels map no longer contains the key `stream`

#### Scenario: RemoveLabel on a missing key is ignored
- **WHEN** `RemoveLabel("stream")` is applied to an Issue whose labels map does not contain the key `stream`
- **THEN** no error is raised
- **AND** the labels map is unchanged

#### Scenario: Full replacement replaces the entire label map
- **WHEN** an Issue whose labels map is `{ "stream": "frontend", "old": "x" }` is updated by full replacement with `{ "module": "auth" }`
- **THEN** the Issue's labels map becomes exactly `{ "module": "auth" }`
- **AND** the previous keys `stream` and `old` are removed

### Requirement: IssueLabelsChanged event carries old and new label maps

When an Issue's label map changes, the Issue SHALL emit an `IssueLabelsChanged` event containing a snapshot of the label map before the change and a snapshot of the label map after the change. A change that leaves the label map identical SHALL NOT emit the event.

#### Scenario: Label set emits before and after maps
- **WHEN** an Issue whose labels map is `{ "stream": "frontend" }` has `SetLabel("stream", "backend")` applied
- **THEN** an `IssueLabelsChanged` event is emitted
- **AND** the event's old map is `{ "stream": "frontend" }`
- **AND** the event's new map is `{ "stream": "backend" }`

#### Scenario: Label removal emits before and after maps
- **WHEN** an Issue whose labels map is `{ "stream": "frontend" }` has `RemoveLabel("stream")` applied
- **THEN** an `IssueLabelsChanged` event is emitted
- **AND** the event's old map is `{ "stream": "frontend" }`
- **AND** the event's new map is empty

#### Scenario: No-op change does not emit an event
- **WHEN** `SetLabel("stream", "frontend")` is applied to an Issue whose labels map already is `{ "stream": "frontend" }`
- **THEN** no `IssueLabelsChanged` event is emitted
