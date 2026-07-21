# Self-Review - Issue #450 Pi Workflow Path

Scope: issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current Session/Runner command protocols, repository architecture, and testing rules. This review modifies no other file.

## Findings

### F-1 High: Completed command receipts have no defined acknowledgement and retention protocol

The revised command flow persists Compact/Reset completion before the Runner releases its command lease, then lets the original public route read the completed outcome (`design.md:129`; `specs/pi-workflow-session/spec.md:95`). Workflow open may proceed past that terminal reservation but must preserve it until the route "reads and acknowledges" it or command-owned cleanup removes it (`design.md:100`; `specs/pi-workflow-session/spec.md:99,148-152`). T-003 requires duplicate completion to return the persisted outcome and the public route to read without repeating the transition, but it defines no receipt-acknowledgement command, consumed state, or retention rule (`tasks.json:68-71,82`).

The current idempotency contract relies on retaining `PendingReset.Outcome` so the same idempotency key can replay the completed result (`AgentSessionGrain.cs:233-241`; `AgentSessionRecoveryRoutes.cs:79-80,122-123`). Clearing when the first route reads it loses that replay if the HTTP response is lost; retaining it indefinitely does not implement the specified acknowledgement/cleanup lifecycle, and the scenario's "read ... exactly once" conflicts with duplicate idempotent reads. Define one durable model: either a separate bounded operation receipt/tombstone with explicit retention and replay semantics, or an acknowledgement protocol that remains safe when the public response is lost. Assign persistence/reactivation and callback -> Workflow open -> public readback -> response-loss tests.

### F-2 Medium: Production command-lease wiring is owned by both T-003 and T-006

T-003 says existing Workflow-origin OpenCode Follow-up/Compact/Reset handlers acquire and retain coordinator leases, and owns their completion callback protocol (`tasks.json:64,67-69`). T-006 again says it will "compose command leases" into those production handlers and repeats that they acquire leases (`tasks.json:156,163`). Because T-006 depends on T-003, either T-003 cannot meet its acceptance criteria when complete, or T-006 reimplements already-delivered production wiring.

Give one task sole production ownership. A clean split is for T-003 to deliver the coordinator, command wire/Server transitions, and fake handler contracts, while T-006 wires existing production handlers together with T-004's outbox drains; alternatively, T-003 can own all lease wiring and T-006 can add only outbox fencing.

### F-3 Medium: Distinct cache-write values lack an explicit Server API projection regression

The Session spec requires distinct `cachedReadTokens` and `cachedWriteTokens` in AgentSession state and API before Web rendering (`specs/pi-workflow-session/spec.md:242-246`). T-003 requires the Server DTO/read-model/mappers to gain the field and tests accumulation plus old-state deserialization (`tasks.json:79,81`), while T-007 tests Web adapters/rendering (`tasks.json:196,200`). No criterion explicitly sends different cache-read/cache-write values through a Server read API and asserts both JSON fields remain distinct.

Add one Server API/read-model regression at the lowest existing API spec surface. This catches an omitted mapper argument or accidental cache-write/cache-read alias that domain accumulation and Web fixtures cannot detect.

### F-4 Medium: The Pi Session-command non-goal is not protected for all four commands

The issue excludes Pi Follow-up, Compact, Reset, and Cancel routing. The design states the non-goal and explicitly prevents only Pi Reset from falling back to OpenCode (`design.md:26-29,104,127`). T-003's notes repeat the four-command boundary, but no acceptance criterion requires route/handler tests proving each Pi-bound command returns unavailable/not-started and never invokes OpenCode (`tasks.json:95`). This matters because T-003 registers runtime `pi` and rewires existing command delivery, while T-006 changes the same production handlers.

Add regressions for Pi-bound Follow-up, Compact, Reset, and Cancel. They should pin the existing unavailable/not-started result, no OpenCode invocation, no binding mutation, and no accidental command lease/outbox side effect.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All seven task IDs and dependencies resolve; the graph is acyclic, priorities are ordered, and every implementation task reaches T-001.
- All referenced spec files and requirement anchors resolve. Comma-separated multi-anchor references are already used by archived plans, so T-003's `spec` string is not treated as a defect.
- All three proposal capabilities have matching spec files and the issue's seven acceptance criteria are represented.
- The prior deadline, cache-write schema, Session admission, stream bootstrap, quarantine release, outbox ownership, omitted-selection, storage-diagnostic, Reset/rebind, and lost-manifest findings are now modeled.
- Pi AgentJob execution, Pi Session-command routing, catalog/UI selection, ACP/RPC, and a generic `AgentRuntime` remain outside implementation scope.

## Verdict

The Pi Workflow path is comprehensively specified, but builders still need to invent command-receipt response-loss semantics, and three boundary regressions/ownership assignments remain incomplete. Resolve these before build.

<promise>FAIL</promise>
