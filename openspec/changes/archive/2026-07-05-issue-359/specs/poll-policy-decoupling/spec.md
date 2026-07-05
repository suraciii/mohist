### Requirement: WorkDispatchResponse no longer carries cleanupPolicy

The server's `WorkDispatchResponse` record SHALL NOT include a `CleanupPolicy` field, and the `POST /api/runner/{runnerId}/poll` handler SHALL NOT populate or serialize a `cleanupPolicy` property on the dispatch body. The `cleanupPolicy` is removed outright (no version compatibility shim), since no cross-version compatibility is required by project guidance.

#### Scenario: poll dispatch body has no cleanupPolicy field

- **WHEN** the server returns a `WorkDispatchResponse` from `POST /api/runner/{runnerId}/poll` (work is dispatchable)
- **THEN** the response body contains the work-dispatch fields (workflowRunId, workId, workType, stage, uses, with, variables, projectId, issueNumber, ownerKind, agentJobId, agentSessionId, recovery, etc.) and does NOT contain a `cleanupPolicy` property.

#### Scenario: runner parses the dispatch without expecting a cleanupPolicy field

- **WHEN** the runner receives a `WorkDispatchResponse` from `poll`
- **THEN** the runner's `WorkDispatchResponse` TypeScript type has no `cleanupPolicy` property, and the connection layer no longer reads `dispatch.cleanupPolicy` when building the work item.

### Requirement: Poll work-dispatch behavior is otherwise unchanged

The `POST /api/runner/{runnerId}/poll` endpoint SHALL retain its existing work-dispatch semantics: when no work is dispatchable it returns `204 No Content` with no body; when work is dispatchable it returns `200 OK` with the `WorkDispatchResponse` body (minus the removed `cleanupPolicy` field). Dispatch routing, owner-kind handling, agent-session id propagation, and recovery fields MUST remain identical to pre-change behavior.

#### Scenario: no-work poll still returns 204 with no body

- **WHEN** the runner calls `POST /api/runner/{runnerId}/poll` and there is no work to dispatch
- **THEN** the server returns `204 No Content` with no response body, exactly as before — the only difference is that this no longer has any cleanup-policy consequence because policy now travels on `/config`.

#### Scenario: dispatchable work still returns a full work envelope

- **WHEN** the runner calls `POST /api/runner/{runnerId}/poll` and work is dispatchable
- **THEN** the server returns `200 OK` with a `WorkDispatchResponse` carrying all the work-execution fields the runner needs (uses, with, variables, workType, stage, title, projectId, issueId, issueNumber, artifacts, setVars, ownerKind, agentJobId, agentSessionId, recovery) — only `cleanupPolicy` is absent.

### Requirement: Runner no longer caches policy from a dispatch

The runner's `ServerConnection` SHALL NOT maintain a `lastCleanupPolicy` field or expose a `getLastCleanupPolicy()` accessor. The `poll()` method SHALL NOT read or store `dispatch.cleanupPolicy`. The runner obtains cleanup policy exclusively via the dedicated config fetch.

#### Scenario: poll does not update any cached cleanup policy

- **WHEN** the runner's `ServerConnection.poll()` receives a dispatch response
- **THEN** no cleanup-policy value is extracted from the response and no internal cleanup-policy cache is updated — `poll()` is purely about work dispatch.

#### Scenario: no getLastCleanupPolicy accessor exists on the connection

- **WHEN** the cleanup-loop tick (`runCleanupOnce`) needs the current cleanup policy
- **THEN** it obtains the policy by calling `ServerConnection.fetchConfig()`, and there is no `getLastCleanupPolicy()` method on `ServerConnection` to fall back to.
