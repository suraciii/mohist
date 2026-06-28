### Requirement: Session followup message endpoint

Server SHALL provide `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` to let a client inject a free-text followup message into a running agent session. The request body SHALL be `{ text: string }` where `text` is non-empty. On success the response SHALL be `200` with `{ status: "sent" }`. The server SHALL validate session state and runner connectivity before accepting the message.

#### Scenario: Active session accepts followup

- **WHEN** a client sends `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` with `{ text: "加个登出" }`
- **AND** the session exists and is in an active (running) state
- **AND** the associated runner is connected
- **THEN** the server SHALL push the message to the runner via SignalR
- **AND** the response SHALL be `200` with `{ status: "sent" }`

#### Scenario: Terminal session rejects followup with 409

- **WHEN** a client sends a followup request for a session in a terminal state (completed, failed)
- **THEN** the server SHALL return `409 Conflict`
- **AND** the error SHALL indicate the session is no longer active

#### Scenario: Runner offline rejects followup with 503

- **WHEN** a client sends a followup request for an active session
- **AND** the associated runner has no active SignalR connection
- **THEN** the server SHALL return `503 Service Unavailable`
- **AND** the error SHALL indicate the runner is offline

#### Scenario: Unknown session rejects followup with 404

- **WHEN** a client sends a followup request and no session exists for the given `{name}`
- **THEN** the server SHALL return `404 Not Found`

#### Scenario: Empty text rejected with 400

- **WHEN** a client sends a followup request with `{ text: "" }`, whitespace-only text, or a missing `text` field
- **THEN** the server SHALL return `400 Bad Request`

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

### Requirement: Create persists workflow profile selection

`POST /api/issues` SHALL accept a `workflowProfileId` field in the request body and SHALL persist it as the issue's workflow profile selection. An explicitly supplied `workflowProfileId` SHALL NOT be silently dropped or replaced by a default. When the field is absent from the request body the issue SHALL have no issue-level selection and reads SHALL resolve the effective profile via default inheritance.

#### Scenario: Create with workflow profile persists it

- **WHEN** the server receives `POST /api/issues` with `workflowProfileId: "mohist/pr"`
- **THEN** the created issue's stored workflow profile selection SHALL be `mohist/pr`
- **AND** `GET /api/issues/:number` SHALL return `workflowProfileId: "mohist/pr"`

#### Scenario: Create without workflow profile inherits default

- **WHEN** the server receives `POST /api/issues` without a `workflowProfileId` key
- **THEN** the issue SHALL have no issue-level workflow profile selection
- **AND** reads SHALL resolve the effective profile to the inherited default

### Requirement: PATCH supports workflow profile selection

`PATCH /api/issues/:number` SHALL apply raw-presence-aware merge semantics to `workflowProfileId`: when the key is absent from the raw request body the issue's workflow profile selection SHALL be preserved; when present with a value the selection SHALL be replaced; when present and `null` the issue-level selection SHALL be cleared so reads fall back to default inheritance. After a successful change, the issue detail, list, and workflow-profile endpoint SHALL all reflect the new selection. The change SHALL NOT alter configured workflow profile variables, prompts, or model/stage overlays.

#### Scenario: Absent workflowProfileId preserves selection

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "body": "new" }` and no `workflowProfileId` key in the raw body
- **THEN** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Present workflowProfileId updates selection

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "workflowProfileId": "mohist/pr" }`
- **THEN** the issue's workflow profile selection SHALL become `mohist/pr`
- **AND** the issue detail, list, and workflow-profile endpoint SHALL report `mohist/pr`

#### Scenario: Null workflowProfileId clears issue-level selection

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "workflowProfileId": null }`
- **THEN** the issue SHALL have no issue-level selection
- **AND** reads SHALL resolve the effective profile via default inheritance

### Requirement: Workflow profile read is consistent across endpoints

The `workflowProfileId` returned by `GET /api/issues/:number`, the issue list endpoint, and the `GET /api/issues/:number/workflow-profile` endpoint SHALL all be the same effective value, resolved from the single source of truth. No endpoint SHALL independently hardcode or recompute a default that diverges from the issue's persisted selection.

#### Scenario: Detail and workflow-profile endpoint agree

- **WHEN** an issue has an issue-level selection of `mohist/pr`
- **THEN** `GET /api/issues/:number` SHALL return `workflowProfileId: "mohist/pr"`
- **AND** `GET /api/issues/:number/workflow-profile` SHALL report profile id `mohist/pr`

#### Scenario: List read model matches detail

- **WHEN** an issue has an effective profile of `mohist/pr`
- **THEN** the issue list endpoint SHALL include `workflowProfileId: "mohist/pr"` for that issue
- **AND** it SHALL match the value returned by `GET /api/issues/:number`

### Requirement: Started issues reject workflow profile selection changes

When an issue has an active workflow run, `PATCH /api/issues/:number` with a present `workflowProfileId` key SHALL be rejected with a clear error stating the issue has started and its execution template cannot be changed. Run-scoped runtime profile overrides (variables/prompts) via the workflow-profile endpoints SHALL remain permitted and SHALL NOT mutate the issue's original template selection.

#### Scenario: PATCH profile rejected on started issue

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "workflowProfileId": "mohist/pr" }`
- **AND** the issue has an active workflow run
- **THEN** the server SHALL return an error with a clear reason
- **AND** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Variable override remains allowed on started issue

- **WHEN** the server receives `PUT /api/issues/:number/workflow-profile/variables` for an issue with an active workflow run
- **THEN** the update SHALL be accepted as a run-scoped runtime override
- **AND** the issue's original workflow profile selection SHALL remain unchanged

### Requirement: Archived issue detail preserves workflow run history

The issue detail API SHALL return the workflow run reference and all associated execution history for an archived issue exactly as it does for a non-archived issue. Setting `archivedAt` SHALL NOT cause the read path to drop `workflowRunId`, the workflow timeline, artifacts, events, feedback, commits, diffs, or execution context from the response. An archived issue's detail response SHALL be sufficient for a client to render the full workflow execution history.

#### Scenario: Archived issue detail returns the workflow run reference

- **WHEN** a client requests the detail of a `Done` issue that was archived after completing workflow run `wr_1`
- **THEN** the response SHALL include `workflowRunId: "wr_1"`
- **AND** the response SHALL include `archivedAt` set to the archive timestamp
- **AND** no execution-history field present before archiving SHALL be absent after archiving

#### Scenario: Archived issue detail exposes workflow timeline and artifacts

- **WHEN** a client requests the detail (or workflow timeline/artifacts sub-resources) of an archived issue
- **THEN** the workflow timeline, artifacts, events, and feedback SHALL be returned
- **AND** the response SHALL be identical in shape to the non-archived detail response

#### Scenario: Archived issue detail is not treated as an active workflow

- **WHEN** a client requests the detail of an archived issue with a preserved `workflowRunId`
- **THEN** the response SHALL NOT indicate an active/running workflow solely because `workflowRunId` is present
- **AND** any active-workflow indicator SHALL reflect the issue's `Done`/archived status

### Requirement: Server exposes workspace cleanup policy to the runner

The server SHALL expose a workspace cleanup policy that the runner can read. The policy SHALL include the retention window (or an explicit unlimited/disabled sentinel) and the storage budget with a target watermark. The server MAY deliver this policy via the runner poll response or a dedicated runner-facing config read. The server MUST NOT scan the runner filesystem, maintain a cleanup queue, or perform runner filesystem deletion; cleanup execution is exclusively a runner-side responsibility.

#### Scenario: Runner reads cleanup policy

- **WHEN** the runner polls or reads its configuration from the server
- **THEN** the response SHALL include a cleanup policy containing a retention window and a storage budget with target watermark
- **AND** the server SHALL NOT instruct or perform any filesystem deletion on the runner

#### Scenario: Server does not scan or schedule runner deletion

- **WHEN** workspace cleanup policy is in effect
- **THEN** the server SHALL NOT enumerate runner workspace directories
- **AND** the server SHALL NOT maintain a per-workspace cleanup queue
- **AND** the server SHALL NOT invoke runner filesystem deletion outside the existing manual cleanup path

### Requirement: Workflow run terminal status is reachable by the owning runner

The server SHALL ensure that a workflow run reaching a terminal state (`completed`, `stopped`, `failed`) is observable by the runner that owns the workspace, both by delivering a workflow run lifecycle event to that runner and by remaining queryable so the runner can converge on missed events. The server is the source of workflow run lifecycle facts; the runner's local marker expresses only workspace identity.

#### Scenario: Terminal event is delivered to the owning runner

- **WHEN** a workflow run reaches a terminal state
- **AND** the owning runner is connected
- **THEN** the server SHALL deliver a workflow run lifecycle event for that run to the owning runner

#### Scenario: Terminal status remains queryable for convergence

- **WHEN** the runner queries the server for the status of an active registry entry's workflow run
- **THEN** the server SHALL return the current lifecycle state of that workflow run
- **AND** the response SHALL distinguish terminal states (`completed`, `stopped`, `failed`) from non-terminal states (`running`, `paused`, `awaiting approval`)

### Requirement: Launch generic AgentSession from an Agent profile endpoint

Server SHALL provide `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` to launch a generic `AgentSession` from a project-scoped Agent profile. The request body SHALL be `{ prompt: string }` where `prompt` is non-empty, and MAY include an optional `context` object carrying context references (issue, epic, repository, workspace path). On success the response SHALL be `201` with `{ sessionId, agentId, agentName, status }`. The endpoint SHALL resolve the Agent in the project, combine the Agent's `Instructions` and `AgentConfig` with the caller's prompt, execute the prompt via a standalone AgentJob that records a generic `AgentSession`, and return the new session identity and current status. The endpoint SHALL be distinct from the validation-only `POST /api/agent-jobs/validate` route, which remains a developer smoke-test surface and SHALL NOT be treated as the product API.

#### Scenario: Launch returns the new session id and status

- **WHEN** a client sends `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` with `{ prompt: "Refactor the auth module" }`
- **AND** the agent resolves in the project
- **THEN** the server SHALL combine the Agent's `Instructions` and `AgentConfig` with the prompt
- **AND** SHALL execute the prompt via a standalone AgentJob that records a generic `AgentSession`
- **AND** the response SHALL be `201` with `{ sessionId, agentId, agentName, status }`

#### Scenario: Launch with optional context references records them as metadata

- **WHEN** a client sends `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` with `{ prompt: "...", context: { issueNumber: 42, repository: "feature-repo", workspacePath: "/repo" } }`
- **THEN** the server SHALL record the supplied context references in the resulting `AgentSession` metadata as prompt context
- **AND** the context references SHALL NOT create scope, mount, or supervisor lifecycle

#### Scenario: Unknown agent is rejected with 404

- **WHEN** a client sends `POST /api/projects/{projectRef}/agents/{agentRef}/sessions` and `{agentRef}` does not resolve to an Agent in the project
- **THEN** the server SHALL return `404 Not Found`
- **AND** no `AgentSession` SHALL be created

#### Scenario: Empty prompt rejected with 400

- **WHEN** a client sends a launch request with `{ prompt: "" }`, whitespace-only prompt, or a missing `prompt` field
- **THEN** the server SHALL return `400 Bad Request`
- **AND** no AgentJob SHALL be submitted

#### Scenario: Launch is distinct from the validation-only agent-jobs route

- **WHEN** a client uses the product launch endpoint
- **THEN** the endpoint SHALL be `POST /api/projects/{projectRef}/agents/{agentRef}/sessions`
- **AND** the endpoint SHALL NOT be the validation-only `POST /api/agent-jobs/validate` route
- **AND** the validation-only route SHALL remain unchanged as a developer smoke-test surface

### Requirement: Generic AgentSession followup endpoint

Server SHALL provide `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup` to inject a free-text followup message into a running generic `AgentSession`. The request body SHALL be `{ text: string }` where `text` is non-empty. On success the response SHALL be `200` with `{ status: "sent" }`. The server SHALL validate session state and runner connectivity before accepting the message. This endpoint SHALL be distinct from the existing issue-scoped `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` route, which SHALL remain unchanged.

#### Scenario: Active generic session accepts followup

- **WHEN** a client sends `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/followup` with `{ text: "add a logout route" }`
- **AND** the generic session exists and is in an active (running) state
- **AND** the associated runner is connected
- **THEN** the server SHALL push the message to the runner via SignalR using a session target that identifies the generic session
- **AND** the response SHALL be `200` with `{ status: "sent" }`

#### Scenario: Terminal generic session rejects followup with 409

- **WHEN** a client sends a followup request for a generic session in a terminal state (completed, failed, stopped)
- **THEN** the server SHALL return `409 Conflict`
- **AND** the error SHALL indicate the session is no longer active

#### Scenario: Runner offline rejects followup with 503

- **WHEN** a client sends a followup request for an active generic session
- **AND** the associated runner has no active SignalR connection
- **THEN** the server SHALL return `503 Service Unavailable`
- **AND** the error SHALL indicate the runner is offline

#### Scenario: Unknown session rejects followup with 404

- **WHEN** a client sends a followup request and no generic session exists for the given `{sessionId}`
- **THEN** the server SHALL return `404 Not Found`

#### Scenario: Empty text rejected with 400

- **WHEN** a client sends a followup request with `{ text: "" }`, whitespace-only text, or a missing `text` field
- **THEN** the server SHALL return `400 Bad Request`

#### Scenario: Issue-scoped followup route remains unchanged

- **WHEN** a client sends `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup`
- **THEN** the server SHALL behave exactly as before this change
- **AND** the issue-scoped route SHALL remain unchanged and distinct from the generic-session followup route

### Requirement: Generic AgentSession cancel endpoint

Server SHALL provide `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/cancel` to request cancellation of a running generic `AgentSession`. The server SHALL attempt to cancel the running turn and SHALL return the resulting session state explicitly. If the underlying agent cannot be cancelled the response SHALL state that the session is not currently cancellable; if the session is already terminal the response SHALL return the current terminal state. The response SHALL NOT pretend success when cancellation is not possible.

#### Scenario: Cancellable active session returns resulting state

- **WHEN** a client sends `POST /api/projects/{projectRef}/agent-sessions/{sessionId}/cancel` for an active generic session whose underlying agent supports cancellation
- **THEN** the server SHALL attempt to cancel the running turn
- **AND** the response SHALL reflect the resulting session state

#### Scenario: Non-cancellable agent is reported honestly

- **WHEN** a client sends a cancel request for an active session whose underlying agent does not support cancellation
- **THEN** the server SHALL return a state indicating the session is not currently cancellable
- **AND** the response SHALL NOT pretend the cancellation succeeded

#### Scenario: Terminal session returns its terminal state

- **WHEN** a client sends a cancel request for a generic session that is already in a terminal state (completed, failed, stopped)
- **THEN** the server SHALL return the current terminal state
- **AND** the response SHALL NOT report a fresh cancellation

#### Scenario: Unknown session rejects cancel with 404

- **WHEN** a client sends a cancel request and no generic session exists for the given `{sessionId}`
- **THEN** the server SHALL return `404 Not Found`
