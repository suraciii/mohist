# Self-Review: Issue 555 External Agent API Plan

Reviewer: pi reviewer (re-review, round 2) verifying the disposition of round 1
findings. Artifacts judged against issue #555's goals/acceptance criteria
(restated in `proposal.md` "What Changes") and the binding capability specs.

## Verdict

**PASS** — round 1's single must-fix (MF-1, stop mapping caller binding) is
properly fixed; no regression was introduced by the fix; no remaining problem
meets the must-fix bar. The plan is ready to build.

## Round 1 finding dispositions (verified)

- **MF-1 — FIXED properly.** `design.md` Decision B now embeds `callerKeyId`
  in the stop `scope_key` (`turnId|callerKeyId|key`), so the `(command,
  scope_key)` unique index yields caller-scoped stop uniqueness; the write
  path discriminates the two contended indexes explicitly; and a new
  cross-caller outcome paragraph defines B's answer in both phases (A pending
  → `409 stop_outcome_unknown`, nothing of B's persisted; resolved → B's own
  durable mapping, evaluated against the Turn's current state — B is never
  served A's row, outcome, or frozen target). Decision D and T-006's
  description/criterion were updated to the same model, and T-004 (which
  creates the schema) now records the caller-bound stop scope, preventing
  MF-1 from being recreated at migration time. This satisfies the binding
  spec (`external-write-idempotency`: "The durable stop mapping MUST
  additionally bind `callerKeyId` … so one caller cannot look up or replay
  another caller's public key"; scenario "Stop keys are caller-bound") and
  the target contract `design/agent-api.md` ("durably maps `(callerKeyId,
  projectId, sessionId, turnId, Idempotency-Key)`"; cross-key `409
  stop_outcome_unknown` while unresolved; matching retry resolves the same
  mapping). MF-1 no longer violates AC6 or the spec's caller-binding MUST.
- **Observations 1–7 — correctly no-action.** All were below the must-fix
  bar. Observation 1's prose ("all 20 keys") was fixed in passing to "all 22
  keys"; verified correct — the spec's allowlist lists exactly 22 keys
  (`projectId` … `sequence`).

## Regression check on the fix

Checked adversarially; none found:

- **Fingerprint excludes the caller (new sentence):** sound. For stop,
  separation comes from the caller-scoped `scope_key`, so two callers'
  identical stops sharing a fingerprint is correct. For launch/follow-up the
  spec deliberately defines scopes *without* caller binding
  (`(projectId, agentId, key)` / `(sessionId, key)`), so cross-caller replay
  inside a grant-authorized Project landing in the shared durable scope is
  spec-conformant and unchanged from round 1 (round-1 Observation 2
  territory).
- **Discrimination-by-index-name:** the only insert that can violate *both*
  unique constraints is the caller's own retry while their own stop is
  still pending; whichever constraint SQLite reports, no duplicate effect
  and no cross-caller leak is possible, and both possible responses (the
  pending-stop resolution path or `409 stop_outcome_unknown`) are defined,
  safe outcomes consistent with the contract's "repeat the same POST"
  recovery model and the 409 table ("the caller must read the Turn"). See
  Observation 2 below for the implementation subtlety.
- **Diff scope:** only `design.md` Decision B/D/E prose and `tasks.json`
  T-004/T-006 changed since round 1 (git: 4afbf4ead, 0a5bb3941); the five
  specs are untouched, and no spec edit was needed since the spec already
  required caller binding. `tasks.json` parses as valid JSON.
- **Codebase anchors re-verified:** `HasFilter` partial-index usage already
  exists in `MohistDbContext.cs` (SQLite partial indexes are an established
  pattern in this repo); `AgentSessionStopOperations`/`ISessionStopDelivery`
  call sites and `StableToken` consumers exist as described.

## Pre-existing problems missed in round 1

Re-swept the fix-adjacent areas (idempotency engine, stop lifecycle
composition, projection/cursor requirements, task ordering) against the
issue's six ACs; nothing new meeting the must-fix bar. The nuances recorded
below (pending-retry response nuance, fingerprint superset, projectId
column) existed in round 1 as well but stay within defined contract
outcomes, so they are recorded as observations rather than justified
must-fix misses.

## Observations (do not affect the verdict)

1. **Binding of `projectId`/`sessionId` on stop rows is transitive.** The
   spec's binding MUST names callerKeyId, projectId, sessionId, turnId; the
   design binds them via `turnId` in the caller-scoped `scope_key` plus
   canonical IDs in `outcome` (turnId functionally determines session and
   project). The mapping row has no explicit `projectId` column. The
   testable substance (caller-binding scenario) is satisfied; T-006 tests
   can assert the target via canonical resolution.
2. **Dual-constraint conflict for own-key stop retry while pending:** when
   one INSERT violates both the `(command, scope_key)` index and the
   per-turn filtered index (caller retrying their own pending stop), the
   reported constraint depends on index evaluation order. Both
   classifications are safe and defined; implementers should resolve it
   deterministically (e.g., attempt the scope-key row load first on any
   conflict) so the own-retry path is stable.
3. **Fingerprint canonical object is a superset for stop/follow-up:** the
   design's object always includes `projectId`, while the spec/contract
   enumerate fingerprint inputs for stop (version, stop, turnId, empty
   body) and follow-up (version, followup, sessionId, body) without it.
   Constant within a scope, deterministic, no observable difference; noting
   for exactness only.
4. Round-1 observations 2–7 (grain-level follow-up key overlap, crash-recovery
   enumeration naming, session-delete tombstone trigger, SpecTests vs
   `test:fast` pairing, deferred rebuild entrypoint, whitespace-only text)
   remain valid as-is and need no action.

## Summary

MF-1 is fixed at the right layer (schema/scope model in Decision B, mirrored
in T-004/T-006), the cross-caller stop outcomes are now explicitly defined
and testable, and the fix introduced no inconsistencies with the specs, the
target contract, or the codebase. Every issue AC (auth-before-anything,
replay-safe writes, five-state reads, cursor resume, public-only shapes,
clear conflict outcomes) has a concrete, verifiable mechanism and task
coverage. The plan is ready to build.

<promise>PASS</promise>
