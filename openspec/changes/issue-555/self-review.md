# Self-Review: Issue 555 External Agent API Plan

Reviewer: pi reviewer (first review, full sweep) against issue #555 and the
plan artifacts (`proposal.md`, `design.md`, `tasks.json`, `specs/`).

## Verdict

**FAIL** — one must-fix problem in `design.md` Decision B: the stop
idempotency mapping's uniqueness/replay model contradicts the binding spec
(and the target contract) on caller binding, making the spec's caller-binding
requirement unimplementable as designed.

## Issue frame (what the plan is judged against)

Issue #555 acceptance criteria:

1. Unauthenticated/unauthorized requests never start Agent execution.
2. Retries of the same request return the original execution, never a
   duplicate.
3. Clients can observe accepted / queued / running / terminal / unknown.
4. After disconnect, reads resume from the original position without
   resubmitting.
5. External callers see only consumer-facing public results.
6. Duplicates, out-of-order delivery, invalid resume, and terminal states all
   have clear, understandable outcomes.

Non-goals: no Slack UX, no external Runner/Runtime Session control, no
redesign of Agent config / Workspace / Workflow Actions.

## Must-fix findings

### MF-1: Stop mapping is not caller-bound in the design's data model — the spec's caller-binding MUST cannot be satisfied

- **Files:** `design.md` Decision B (idempotency table, unique index, write
  path), vs `specs/external-write-idempotency/spec.md` ("Durable keyed
  mappings are scoped per command" — stop binding — and scenario "Stop keys
  are caller-bound") and `design/agent-api.md` ("the first keyed request
  durably maps `(callerKeyId, projectId, sessionId, turnId, Idempotency-Key)`
  to one canonical per-target stop operation").
- **Problem:** The spec (binding per the proposal) and the target contract
  both require the durable stop mapping to bind `callerKeyId` so "one caller
  cannot look up or replay another caller's public key". But `design.md`
  Decision B defines the stop `scope_key` as `turnId|key` with a single unique
  index on `(command, scope_key)`, and its write path says: on index conflict,
  load the existing row; same fingerprint → replay path. The stop fingerprint
  is `version + stop + canonical turnId + empty body` — identical for every
  caller — so under the design as written, caller B presenting caller A's
  `(turnId, key)` always lands in the replay path and receives A's mapping and
  outcome. B can never obtain "B's own durable mapping" (the unique index
  forbids a second row), so the spec scenario "caller B's request is evaluated
  against caller B's own durable mapping, not caller A's" is unimplementable
  under the literal design, and T-006's own acceptance criterion ("caller B
  replaying caller A's public key ... is evaluated against caller B's own
  durable mapping") cannot pass. The cross-caller outcome (which HTTP status
  B receives, and what row represents B's request) is defined nowhere in the
  plan.
- **Criteria violated:** AC6 (duplicates/conflicting requests must have
  clear, defined outcomes) and the change's own binding capability spec
  (`external-write-idempotency`, caller-binding MUST).
- **Fix direction (small):** make stop uniqueness caller-scoped (e.g., embed
  `callerKeyId` in the stop `scope_key`, or a stop-specific unique index on
  `(command, scope_key, caller_key_id)`), keep the filtered pending-per-turn
  index as the cross-caller `409 stop_outcome_unknown` block, and state the
  response B gets (own mapping created; classification then yields no-op /
  conflict per turn state). Update `design.md` Decision B and T-006's
  description accordingly.

## Dimension review (first review, full sweep)

### Coverage — checked, one gap (MF-1)

Every issue AC is addressed: AC1 → `external-agent-caller-auth` (bearer-only,
grant-before-lookup, zero-effect 401/403) + T-001; AC2 →
`external-write-idempotency` (durable scoped mappings, replay, one execution
per key) + T-004/5/6; AC3 → `public-execution-read` five-state aggregate +
T-002/3; AC4 → `public-session-event-stream` exclusive-after cursors plus
Job-read/launch-replay recovery + T-007/3/4; AC5 → strict
`PublicExecutionRead`/`PublicEventPage` allowlists + contract tests +
ArchTests rule; AC6 → error table, 409s, terminal fences, durable-rejection
200s — except the cross-caller stop-key outcome, undefined (MF-1). The inert
PAT grant from the prior slice is consumed (T-001), and docs
(`design/agent-api.md`, `design/auth.md`, `docs/*`, README implementation
table) are covered by T-008. All five capability specs trace to tasks with
valid requirement anchors; all seven routes from `design/agent-api.md` are
implemented across T-003..T-007. Non-goals respected (no Runner/Runtime
surface, stop adapts the canonical fenced lifecycle, no Slack/config scope).

### Correctness — checked, one flaw (MF-1); approach otherwise sound

- Auth ordering is structurally guaranteed (dedicated middleware before any
  endpoint delegate; `AuthResolutionMiddleware` already loads the `Credential`
  including `DirectApiProjectGrant`, verified in
  `src/Mohist.Server/Auth/Identity/AuthResolutionMiddleware.cs`), so 401/403
  paths cannot touch idempotency/admission.
- Launch idempotency composes `IAgentLauncher.LaunchIdempotentAsync` with a
  derived coordinator key in a distinct `StableToken` namespace — the Web
  surface's `(projectId, key)` grain identity is untouched; crash between
  mapping insert and completion re-enters the coordinator's own dedup.
- Follow-up determinism holds: the session grain's existing keyed
  `AlreadyAccepted`/content-mismatch path (verified in
  `AgentSessionGrain.cs`) plus deterministic pre-minted Input/Turn IDs gives
  at-most-one pair per mapping; note the grain's idempotency dedup already
  pre-mints IDs the same way in the Web follow-up route.
- Stop composition reuses `AgentSessionStopOperations.StopAsync` unchanged
  (terminal no-op / queued local cancel / launch-turn job cancel / claim with
  `expectedOperationId` / dispatch / apply — all verified present), and the
  filtered pending-per-turn index is a sound cross-caller block.
- Projection correctness (one-transaction snapshot+journal+checkpoint,
  terminal fences, checkpoint crash recovery, generation switching with a
  global sequence allocator, `503 projection_lag` freshness gate, HMAC
  generation-bound cursors with 400/410 and tombstone behavior) matches the
  target contract `design/agent-api.md` requirement-for-requirement; nothing
  was renegotiated.
- Except for MF-1, each AC's satisfaction mechanism is concrete and testable.

### Consistency with the codebase — checked, no issue

All referenced building blocks exist as described: `DirectApiProjectGrant`
(Explicit/OperatorAll) on `Credential`; `RouteScopeRequirement`;
`AgentJobEventRow`/`AgentSessionEventRow`/`EventStore`; `AgentLaunchCoordinatorCodec.StableToken`;
`Mohist.Server.SpecTests`/`Mohist.Server.ArchTests`; SQLite/EF additive-migration
convention; `docs/agent-api.md` "Implementation Gaps" section and README
"Implementation Status" table exist for T-008 to update. Route placement under
`Api/DirectApi/` and the SpecTests layout match existing conventions.
`tasks.json` schema (fields, `spec` anchors, AFK/WRITE, `passes:false`) matches
the repo's existing plans (e.g., issue-589).

### Task breakdown — checked, no issue

Ordering is sound: T-002 (projection) is deliberately independent of T-001;
T-003 builds the shared lag-check/response plumbing that T-004..T-007 reuse;
commands depend on the engine (T-004); docs last with the full `npm run
verify` gate. Every task has concrete, verifiable acceptance criteria
including pinned pipeline-order and crash-recovery SpecTests; T-004 creates
the full idempotency schema (including `frozen_target` and the partial index)
to avoid a third migration. One schema inaccuracy is recorded as an
observation below, not a breakdown defect.

## Observations (do not affect the verdict)

1. **`design.md` Decision E miscounts the allowlist:** says "all 20 keys";
   the spec and `design/agent-api.md` list 22 `PublicExecutionRead` keys
   (projectId…sequence). The specs/tasks say "every listed key" and the DTO
   has required members + round-trip tests, so the mechanism is unaffected —
   fix the prose when touching the file.
2. **Follow-up key namespace overlaps the Web surface at the grain level:**
   the session grain dedups follow-ups by `(session, idempotencyKey)` across
   all sources; the direct route passes the public key through, so a Web/CLI
   recovery caller using the same key string on the same Session would make
   the grain throw on content mismatch (500 + pending mapping row — safe
   failure, no duplicate). Launch isolates via a derived namespace; consider
   a namespaced internal key for follow-up too.
3. **Crash-recovery enumeration in Decision B omits follow-up:** it names
   launch (idempotent grain) and stop (pending resolution); the follow-up's
   pending row is recovered by the grain's `AlreadyAccepted` path, which the
   design invokes but does not name. Cosmetic completeness nit.
4. **Session-delete → stream-close tombstone has no current trigger:** no
   control-plane AgentSession delete route exists today, so the closed-flag
   write path is unexercised until one does; T-007's tombstone tests will
   need to simulate closure. Conditional-future behavior per the spec; fine,
   but worth knowing while implementing T-002's `public_stream_states`.
5. **`npm run test:fast` does not run SpecTests** (unit/arch/wf/cli/web/runner
   only); tasks pair "SpecTests cover X" with "test:fast passes", relying on
   T-008's full `npm run verify` for the coordinated gate. Matches existing
   repo convention (issue-570/589 use the same phrasing); informational.
6. **Operator-triggered rebuild entrypoint deferred** (design Open Questions,
   T-002 note) while generation-switching machinery ships — an explicitly
   conscious scope choice; ensure T-008 docs do not describe the entrypoint
   as shipped (T-008 notes already say so).
7. **Whitespace-only `text` accepted** per the byte-significant rule — already
   self-flagged as a design open question; consistent with the spec as
   written ("non-empty" only).

## Summary

The plan is unusually thorough and well-anchored: it implements the existing
target contract rather than renegotiating it, reuses verified canonical
owners, and its task breakdown is complete and testable. The single must-fix
(MF-1) is narrow — the stop mapping's caller binding must be made real in
`design.md`'s uniqueness/replay model and the cross-caller outcome defined —
after which the plan is ready to build.

<promise>FAIL</promise>
