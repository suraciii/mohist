# Self-Review — Issue #489 (Issue 关注 / watch), round 2

**Reviewer:** plan-stage self-review (reviewer, not fixer)
**Artifacts reviewed:** `proposal.md`, `design.md`, `tasks.json`, `specs/issue-watch/spec.md`,
`specs/issue-watch-dispatch/spec.md`, cross-checked against issue #489 and the fixed contract
`design/issue-watch.md`.
**Verdict:** **PASS** — the round-1 blocker is resolved cleanly and consistently; the plan is
ready to build. A few low-severity informational notes remain (non-blocking).

## Round-1 findings — resolution check

- **F1 (single-launch idempotency scope) — RESOLVED.** The guarantee is now explicit and
  consistent in three places: `specs/issue-watch-dispatch/spec.md` (*Per-agent launch
  idempotency* + *Event replay does not double-launch* scenario scoped to "redelivered under
  unchanged dispatch configuration", with cross-delivery source mutation called out as
  out-of-scope + rationale), `design.md` D7 (guarantee scope + the cross-source edge + the
  follow-up path if strict cross-source is ever needed), and `tasks.json` T-003 (criterion
  sharpened to "within-delivery HashSet dedup … same-config replay via grain first-writer …
  cross-delivery source mutation out of scope"). The carve-out is defensible: fully deduping
  cross-delivery source mutation requires a per-`(eventId, agentId)` launch ledger on the
  routing path, which conflicts with the issue's own Non-Goal ("touching routing-rule
  semantics"). It is also consistent with issue #489's acceptance criterion #5, which only
  requires the *simultaneous* rule+watch case ("同时命中时只启动一次") — exactly the
  within-delivery case the HashSet guarantees.
- **F2 (watch list mapping + render shape) — RESOLVED.** D9 pins `watch list` as a focused
  two-group render (not full `IssueShow`); the open question is removed; T-004 references the
  `watch list` requirement and has a dedicated acceptance criterion for the focused render.
- **F3 (provenance is a prefix convention) — RESOLVED.** D8 and the T-003 criterion now
  explicitly state `TriggerRuleId` carries a `watch:`-prefixed value as a string-prefix
  convention, not a typed marker.
- **F4 (end-to-end dispatch coverage) — RESOLVED.** T-003 acceptance now states the seeded
  spec tests assert the full event→watch-launch behavior (rule+watch single-launch and
  same-config replay), covering the dispatch capability end-to-end at the spec level despite
  no single task owning the CLI→dispatch path.

## Fresh re-review — no new blockers

I re-checked the updated artifacts for contradictions introduced by the fixes and for anything
missed in round 1:

- **Mechanism correctness.** D5/D7's flow is sound: the rule loop runs to completion
  (populating the launched-agent set, applying muted suppression per rule hit), then the watch
  pass skips any agent already launched. There is no within-delivery ordering where a watch
  launch would precede a rule launch for the same agent, so the set correctly prevents the
  double-launch. Muted and watching cannot coexist on one `(issue, agent)` triple (D1 unique
  index), so suppression and launch never conflict.
- **`watch:` provenance collision.** Rule ids are `rule_{Guid:N}`; watch passes
  `watch:{agentId}` (`agent_{Guid:N}`). Distinct namespaces — no collision. ✓
- **Hot-path change (removing the `rules.Count == 0` early return).** Guarded by
  `evt.Type ∈ fixed set` AND issue presence, so the common case exits early; backed by the
  "Event without issue does not trigger watch" scenario. ✓
- **DAG / dependencies.** T-001→{T-002, T-003}, T-002→T-004; valid, acyclic, all deps point
  to strictly lower priorities. `tasks.json` is valid JSON. ✓
- **Task split.** Capability-aligned (data layer / server command+projection / dispatch /
  human surface); no over-granular technical-step tasks; every task carries test criteria. ✓

## Coverage check — issue acceptance criteria → tasks

| # | Acceptance criterion | Covered by | Status |
|---|---|---|---|
| 1 | `watch add` → `issue view` / `watch list` show 关注 | T-002 (projection), T-004 (render) | ✅ |
| 2 | Watched issue at approval / run-failed → launched (trigger label = watch) | T-003 + D8 | ✅ |
| 3 | `watch remove` stops launch; rule-covered shows 静音, others unaffected | T-002 (mute), T-003 (issue-scoped suppression) | ✅ |
| 4 | Re-`watch add` on muted lifts the mute | T-001 (muted→watching) | ✅ |
| 5 | Same event, same Agent via rule + watch → launched once | T-003 (D7, within-delivery) | ✅ |

All five criteria are covered; criterion #5 is satisfied within the scope the issue actually
asks for (simultaneous coincidence).

## Informational notes (non-blocking)

- **Contract wording vs implementation.** The persistent contract `design/issue-watch.md:63`
  states the idempotency key is literally `hash(projectId, eventId, agentId)`. The
  implementation uses per-source grain keys `(projectId, eventId, ruleId)` plus handler-level
  within-delivery dedup — equivalent *effect* within an event, but the contract's literal
  "key = hash(…agentId)" is a simplification. Not a blocker: D7 is authoritative for this
  change and the effect matches the contract's intent. Worth reconciling the (wip) contract
  doc's wording later so future readers aren't misled.
- **Goal/Constraint precision.** `design.md` line 35 ("at most once per event") and line 45
  describe the within-delivery guarantee and are accurate for the in-scope cases; D7 is the
  authoritative precise statement. Optional polish: append "within a single delivery" to line
  35 for full consistency with D7's scoped wording.
- **Replay testability.** Same-config replay dedup relies on grain first-writer (Orleans
  infra). T-003's handler-level tests verify within-delivery dedup via a fake launcher; the
  replay guarantee is trusted to grain-level coverage (or a fake `EnsurePreparedAsync`
  simulating first-writer). This is a reasonable split, not a gap.

## Per-artifact notes

- **proposal.md** — unchanged; impact map still matches the two capability specs. No drift.
- **design.md** — D7 now carries the explicit guarantee scope and rationale; D8/D9 reflect the
  F2/F3 resolutions. Decisions remain concrete with file:line anchors and alternatives.
- **tasks.json** — criteria sharpened; valid DAG; spec references point to real requirements.
- **specs** — the dispatch spec's idempotency requirement is now precise and self-consistent;
  the `issue-watch` spec is unchanged and complete.

<promise>PASS</promise>
