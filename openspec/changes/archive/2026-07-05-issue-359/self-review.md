# Self Review Report

## Result: PASS

The plan for issue-359 (separate the runner config channel so `cleanupPolicy` no longer piggybacks on work dispatch) was reviewed against the issue body, `proposal.md`, `design.md`, `tasks.json`, and the three spec files. Code-level claims (line numbers, file paths, existing types) were verified against `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs`, `packages/runner/src/server/connection.ts`, `packages/runner/src/runtime/host.ts`, `packages/runner/src/runtime/cleanup-loop.ts`, `packages/runner/src/core/types.ts`, `CleanupPolicyOptions.cs`, and the referenced test files. No blocking issues and no safe repairs were found.

## Repaired Items

(none — no artifacts needed changes)

## Blocking Items

(none)

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: completeness
  Evidence: The `fetchConfig` failure / best-effort behavior (design D4: throws on non-2xx or network error, the existing `runCleanupOnce` try/catch at `host.ts:182-185` logs and skips the tick, no stale fallback) is captured in `design.md` D4 and in T-002's acceptance criteria, but `specs/runner-config-fetch/spec.md` has no dedicated scenario asserting "transient `/config` failure skips the tick and retries next tick". All other idle-system scenarios are present.
  SuggestedAction: Optionally add one scenario to the `runner-config-fetch` spec asserting the fetch-failure → log-and-skip-tick → retry-next-tick behavior, so the requirement is readable from the spec alone rather than only from design + task AC. Not blocking: the behavior is already specified and testable via the task AC.
  Status: follow-up

## Detailed Findings

### Alignment
- All 7 issue Acceptance Criteria trace to plan content:
  - AC1 (dedicated `GET /config` endpoint) → capability `runner-config-endpoint` + spec + T-001.
  - AC2 (idle cleanup still runs) → `runner-config-fetch` spec scenario "idle system with a configured policy still performs cleanup".
  - AC3 (poll contract; field keep-or-remove decided) → decision is **remove outright** (design D2), captured in `poll-policy-decoupling` spec; the existing TS field is optional so removal is non-breaking to the runner's parser.
  - AC4 (single config source) → `runner-config-endpoint` spec mandates "runner MUST NOT read `config.jsonc` directly".
  - AC5 (per-tick pull, no ETag/version) → `runner-config-fetch` scenario "fetch frequency is one GET per cleanup-loop cycle with no caching or version negotiation".
  - AC6 (testing per `design/testing.md`, no real HTTP/time) → both tasks' ACs require test-client / fake connection / no real time.
  - AC7 (medium-risk driver = cross-plane wire-contract edit) → `design.md` "Risks / Trade-offs" + proposal "Impact".
- All issue Non-Goals (no algorithm/cadence change, no #355 hot-reload, no ETag/watch, no runner reading config files) are reflected in design Non-Goals and the "Cleanup-loop cadence and algorithm are unchanged" spec requirement.
- The 4-row "改动点" change table in the issue maps 1:1 to T-001 (add endpoint + DTO) and T-002 (runner switch + atomic field removal on both ends).

### Completeness
- Every capability in the proposal has a matching spec file: `runner-config-endpoint`, `runner-config-fetch`, `poll-policy-decoupling`.
- Every spec has at least one task: T-001 covers `runner-config-endpoint`; T-002 covers `runner-config-fetch` + `poll-policy-decoupling`.
- Edge cases handled: fully-unconfigured policy (all-null body still 200), null sentinels, idle system with configured policy, idle system with unconfigured policy, dispatchable-work still returns full envelope, no-work still 204. The `/config`-fetch failure case is covered in design D4 + task AC (see follow-up item-1 for the spec-scenario gap).

### Consistency
- Spec requirement headings match capability names; design decisions D1–D5 map cleanly onto spec requirements.
- Task spec-anchor references all resolve to real headings:
  - T-001 `#dedicated-runner-config-endpoint` → "### Requirement: Dedicated runner config endpoint".
  - T-002 `#runner-fetches-config-on-each-cleanup-loop-tick` → "### Requirement: Runner fetches config on each cleanup-loop tick".
  - T-002 `#workdispatchresponse-no-longer-carries-cleanuppolicy` → "### Requirement: WorkDispatchResponse no longer carries cleanupPolicy".
- Naming consistent across all artifacts: `RunnerConfigResponse`, `CleanupPolicyDto`, `ToCleanupPolicyDto`, `CleanupPolicyOptions` (server) and `fetchConfig`, `RunnerConfigResponse`, `CleanupPolicy` (runner). JSON field is `cleanupPolicy` everywhere.

### Feasibility
- Verified code claims (all accurate):
  - `RunnerRoutes.cs:18` — existing `/api/runner/{runnerId}` route group; `:92-117` — `/poll` handler; `:96` — `Results.NoContent()` when idle; `:115` — `CleanupPolicy: ToCleanupPolicyDto(...)`; `:455` — `ToCleanupPolicyDto`; `:532` — `WorkDispatchResponse`; `:556` — `CleanupPolicyDto? CleanupPolicy` field.
  - `connection.ts:9` — `lastCleanupPolicy` field; `:32` — `this.lastCleanupPolicy = dispatch.cleanupPolicy ?? null`; `:36` — `getLastCleanupPolicy()`; `:302-303` — `url(path)` builder that yields `/api/runner/{runnerId}/{path}` (so `this.url("config")` produces the target endpoint with no new URL plumbing).
  - `host.ts:173-186` — `runCleanupOnce`; `:175` reads `getLastCleanupPolicy()`; `:182-185` existing try/catch that D4 reuses for config-fetch errors.
  - `types.ts:94` — `cleanupPolicy?` on `WorkDispatchResponse`; `:104` — reusable `CleanupPolicy` interface.
  - `cleanup-loop.ts:43` — `if (!policy) return result` short-circuit (the idle-gap bug surface).
  - The three host test mocks expose `getLastCleanupPolicy = () => null` (`runner-host.spec.ts:57`, `runner-host-task-log.spec.ts:49`, `runner-host-convergence.spec.ts:46`).
  - `packages/web/` has no references to `cleanupPolicy`/`WorkDispatchResponse`/`CleanupPolicy` — the web typecheck/test requirement in T-002 is a pure safety net, no web edits needed (and none claimed).
- Task granularity is appropriate: two tasks for an `effort: small` cross-plane refactor. T-001 is a purely-additive endpoint slice; T-002 bundles the runner switch + the server-side field removal because design D2 + migration-plan step 3 require them to land atomically (a runner reading `dispatch.cleanupPolicy` against a server that dropped the field, or vice-versa, is the exact inconsistency the atomic landing avoids). Splitting T-002 would violate the design's atomicity invariant, so the grouping is correct, not over-coarse. No task title is a low-level technical action ("定义接口"/"提取类"/"注册DI"/"创建文件"); tests are embedded in each task's ACs rather than split out.

### Dependency Completeness
- T-001 `dependsOn: []`, priority 1 (first task).
- T-002 `dependsOn: ["T-001"]`, priority 2 — references an existing lower-priority task.
- The dependency is real: the runner can only `fetchConfig` from `/config` after T-001 ships the endpoint.
- No cycles; linear chain.

<promise>PASS</promise>
