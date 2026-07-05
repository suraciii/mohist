### Requirement: Dedicated runner config endpoint

The server SHALL expose `GET /api/runner/{runnerId}/config` that returns a `RunnerConfigResponse` body describing runner-side configuration. The endpoint MUST be reachable independently of whether any work is currently dispatchable on `POST /api/runner/{runnerId}/poll`, and MUST NOT require a successful poll to have recently occurred.

#### Scenario: config endpoint returns 200 with a body when work is dispatchable

- **WHEN** a runner calls `GET /api/runner/{runnerId}/config` while the server also has work to dispatch on `poll`
- **THEN** the server returns `200 OK` with a JSON body of shape `RunnerConfigResponse { cleanupPolicy }`, regardless of the poll queue state.

#### Scenario: config endpoint returns 200 with a body when the system is idle

- **WHEN** a runner calls `GET /api/runner/{runnerId}/config` while `poll` would return `204 No Content` (no work dispatchable, system idle)
- **THEN** the server STILL returns `200 OK` with a `RunnerConfigResponse { cleanupPolicy }` body, so policy availability is never gated by work presence.

#### Scenario: config endpoint is a plain GET with no request body

- **WHEN** a runner issues `GET /api/runner/{runnerId}/config`
- **THEN** the request is a lightweight GET with no request body required, and no ETag / If-None-Match / version negotiation is performed.

### Requirement: RunnerConfigResponse carries cleanupPolicy derived from CleanupPolicyOptions

The `RunnerConfigResponse` SHALL contain a single `cleanupPolicy` field whose value is produced by projecting the server's bound `CleanupPolicyOptions` (from the `Mohist:WorkspaceCleanup` config section) through the existing `ToCleanupPolicyDto` mapping. The server SHALL remain the single source of truth for the policy; the runner MUST NOT read `config.jsonc` directly.

#### Scenario: configured retention and budget are projected into the response

- **WHEN** the server's `CleanupPolicyOptions` has `RetentionDays` and `StorageBudgetBytes` configured (and optionally `StorageTargetWatermarkBytes`)
- **THEN** `GET /api/runner/{runnerId}/config` returns a `cleanupPolicy` object whose `retentionDays`, `storageBudgetBytes`, and `storageTargetWatermarkBytes` reflect those configured values.

#### Scenario: unconfigured policy fields are emitted as null sentinels

- **WHEN** one or more `CleanupPolicyOptions` fields are unset (or set to non-positive values)
- **THEN** the corresponding `cleanupPolicy` fields in the response are `null`, preserving the existing "null means unlimited / disabled" contract — the runner never has to distinguish "disabled" from "missing".

#### Scenario: fully-unconfigured policy yields a response with null fields

- **WHEN** `CleanupPolicyOptions` has no fields configured (default/unconfigured server)
- **THEN** `GET /api/runner/{runnerId}/config` still returns `200 OK` with a `RunnerConfigResponse` whose `cleanupPolicy` fields are all `null`, rather than omitting the body or returning an error.

### Requirement: Reuse of CleanupPolicyDto shape

The `cleanupPolicy` field in `RunnerConfigResponse` SHALL use the existing `CleanupPolicyDto` record (`{ retentionDays, storageBudgetBytes, storageTargetWatermarkBytes }`, all nullable) — the same shape already produced by `ToCleanupPolicyDto`. No new policy field semantics, no new sentinel values, and no new fields SHALL be introduced by this endpoint.

#### Scenario: response policy shape matches the existing CleanupPolicyDto

- **WHEN** the server serializes `RunnerConfigResponse.cleanupPolicy`
- **THEN** the JSON object shape is identical to the existing `CleanupPolicyDto` (`retentionDays`, `storageBudgetBytes`, `storageTargetWatermarkBytes`, each nullable), so the runner can parse it with the existing `CleanupPolicy` TypeScript type.
