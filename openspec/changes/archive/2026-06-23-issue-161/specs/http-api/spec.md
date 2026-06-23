## MODIFIED Requirements

### Requirement: Issue label API uses key-value model

The HTTP API SHALL treat Issue labels as key-value pairs governed by the `issue-labels` capability. `POST /api/issues` and `PATCH /api/issues/:id` SHALL accept a `labels` field as a key-value map. On `PATCH /api/issues/:id`, the `labels` field SHALL follow raw-presence-aware merge semantics: when `labels` is absent from the raw request body the issue's existing label map SHALL be preserved unchanged; when `labels` is present and `null` or empty the label map SHALL be cleared; when `labels` is present and a non-null object the label map SHALL be replaced in full. `GET /api/labels` SHALL return the distinct label keys used across the current project's issues, so surfaces can present the available classification dimensions.

#### Scenario: Absent labels field preserves existing labels

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "body": "new body" }` and the raw request body does not contain a `labels` key
- **THEN** the issue's existing label map SHALL remain unchanged
- **AND** no `IssueLabelsChanged` event SHALL be emitted for labels

#### Scenario: Null labels field clears labels

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "labels": null }`
- **THEN** the issue's label map SHALL become empty
- **AND** an `IssueLabelsChanged` event SHALL be emitted if the prior map was non-empty

#### Scenario: Present labels field replaces label map in full

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ labels: { "module": "auth" } }`
- **THEN** the issue's label map becomes exactly `{ "module": "auth" }`
- **AND** any previously present keys are removed

#### Scenario: Label set by key persists a single value

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ labels: { "stream": "backend" } }` for an issue whose `stream` was `frontend`
- **THEN** the issue's label map contains `{ "stream": "backend" }`
- **AND** the prior value `frontend` is no longer present

#### Scenario: GET labels returns distinct keys

- **WHEN** the client requests `GET /api/labels` for a project whose issues use keys `stream`, `module`, and `stream`
- **THEN** the response contains the distinct keys `stream` and `module`
- **AND** the keys conform to the label key validation rule

## ADDED Requirements

### Requirement: PATCH omit-means-unchanged for all optional fields

`PATCH /api/issues/:number` SHALL apply raw-presence-aware merge semantics to every optional field. A field that is absent from the raw request body SHALL preserve the issue's stored value. A field that is present and `null` SHALL clear the stored value when the field is nullable. A field that is present with a value SHALL replace the stored value. This contract SHALL apply uniformly to scalar fields (title, body, priority), nullable fields (isDraft), and collection fields (labels, attachmentIds). The server SHALL distinguish raw-body key presence from deserialized `null`/default values so that omitted fields are never mistaken for explicit clears.

#### Scenario: Absent isDraft preserves draft state

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "title": "New title" }` and the raw request body does not contain an `isDraft` key
- **AND** the issue's current `isDraft` is `true`
- **THEN** the issue's `isDraft` SHALL remain `true`

#### Scenario: Present isDraft updates draft state

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "isDraft": false }`
- **THEN** the issue's `isDraft` SHALL become `false`

#### Scenario: Absent attachmentIds preserves attachments

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "body": "updated" }` and the raw request body does not contain an `attachmentIds` key
- **AND** the issue has existing attachment ids `[ "att_1", "att_2" ]`
- **THEN** the issue's `attachmentIds` SHALL remain `[ "att_1", "att_2" ]`

#### Scenario: Null attachmentIds clears attachments

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "attachmentIds": null }`
- **THEN** the issue's `attachmentIds` SHALL become empty

#### Scenario: Present attachmentIds replaces list

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "attachmentIds": [ "att_3" ] }`
- **THEN** the issue's `attachmentIds` SHALL become exactly `[ "att_3" ]`

#### Scenario: Absent priority preserves priority

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "labels": { "k": "v" } }` and the raw request body does not contain a `priority` key
- **AND** the issue's current priority is `p1`
- **THEN** the issue's `priority` SHALL remain `p1`

#### Scenario: PATCH only the fields present in the raw body

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "labels": { "stream": "backend" } }` and no other keys in the raw body
- **THEN** title, body, priority, isDraft, and attachmentIds SHALL each remain unchanged
- **AND** only the label map SHALL be replaced
