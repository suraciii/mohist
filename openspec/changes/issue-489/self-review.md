# Self-Review — Issue #489 (Issue 关注 / watch)

**Reviewer:** plan-stage self-review (reviewer, not fixer)
**Artifacts reviewed:** `proposal.md`, `design.md`, `tasks.json`, `specs/issue-watch/spec.md`,
`specs/issue-watch-dispatch/spec.md`, and the fixed contract `design/issue-watch.md`.
**Verdict:** **FAIL** — one substantive correctness gap in the central mechanism must be
resolved before/while building; the rest is consistent and well-scoped.

## Summary

The plan is internally coherent: proposal → design → tasks → specs align, the task DAG is
valid, every issue acceptance criterion maps to a task, and each decision carries an
alternative. The riskiest part of this change is the **per-event single-launch guarantee**
(one Agent launched at most once when a routing rule and a watch coincide). The design's
chosen mechanism (D7) is sound for the within-delivery collision and for same-configuration
replay, but it does not fully cover the spec's explicit *"Event replay does not double-launch"*
scenario under configuration change between deliveries. That gap must be closed explicitly —
either by a durable `(eventId, agentId)` guard or by scoping the guarantee in writing and
confirming the spec scenario is still met. This is the only blocker.

## Findings

### F1 — Single-launch idempotency does not cover cross-source replay (Medium, must address)

**Spec** (`specs/issue-watch-dispatch/spec.md`, requirement *Per-agent launch idempotency*):
launch idempotency SHALL be normalized to `(projectId, eventId, agentId)`; scenario
*"Event replay does not double-launch"* — *"the same event (same eventId) is processed more
than once for the same Agent → launched at most once; no duplicate AgentJob."*

**Design D7** enforces the collision with an **in-memory `HashSet<agentId>`** inside one
`DispatchAsync` call, and delegates replay safety to grain first-writer keyed on
`(projectId, eventId, ruleId)` — explicitly **per launch source**, not per agent. The design
is honest about this ("protect against event replay for each distinct launch source") and
D7 only quotes the within-event scenario.

Coverage of the two real cases:
- **Rule + watch on one event (same delivery):** covered by the `HashSet`. ✅
- **Same-config replay (redelivery):** covered by grain first-writer per source. ✅
- **Cross-delivery with source mutation:** *not covered.* If delivery 1 launches Agent X via
  a routing rule (grain key `…rule_R`), the rule is then removed and a watch added, and the
  event is redelivered, the watch pass launches X under a *different* grain key (`…watch:X`).
  First-writer does not cross-dedupe → a **second AgentJob** is created. The spec's
  *"at most once / no duplicate AgentJob"* scenario does not carve out configuration change.

**Why it matters:** this is the heart of the change and an explicit, testable spec scenario.
The realistic replay (no config change) is fine, but the plan currently neither guarantees
cross-source at-most-once nor explicitly marks the cross-source case as an accepted exception.

**Required resolution (pick one, then reflect it in T-003's acceptance criteria + tests):**
- **(a)** Add a durable per-event launched-agent guard consulted before every
  `LaunchRoutedAsync` (e.g. a `(projectId, eventId, agentId)` set persisted for the event
  lifetime, or a single canonical grain key per `(eventId, agentId)` regardless of source); or
- **(b)** Explicitly scope the guarantee in the design: *"at most once per event under
  unchanged dispatch configuration; cross-delivery source mutation is out of scope,"* and
  confirm that satisfies the spec's intent (the spec author should ratify this reading).

Either closes the over-claim. T-003 today asserts "replaying the same eventId does not create a
second AgentJob (grain first-writer)" — which is only true **per source**, so that criterion
must be sharpened to match whichever resolution is chosen.

### F2 — `watch list` requirement mapping and render shape (Low)

The spec requirement *`watch list`* is delivered by T-004, but T-004's `spec` field cites only
*Watch projection in issue detail*. `watch list` is also listed as an open question in the
design (its render shape — full `IssueShow` vs a compact list — is undecided). Not blocking,
but: (1) T-004 should reference the *`watch list`* requirement too; (2) the render-shape
decision should be pinned in T-004 so it isn't left to the implementer's discretion at runtime.

### F3 — Provenance is a string-prefix convention, not a typed marker (Low, informational)

D8 records the watch source by stuffing a `watch:`-prefixed value into the existing
`TriggerRuleId` label. This satisfies the spec's *"distinguishing it from a routing-rule
launch"* only by convention; any downstream query/filter that keys on `TriggerRuleId` must
know to substring-match the prefix. Acceptable for this issue (a dedicated `TriggerSourceKind`
is a documented deferred open question) — no action required now, but worth a one-line note in
T-003 so future tooling isn't surprised.

### F4 — No single task owns the end-to-end watch→dispatch path (Low, informational)

Issue acceptance criterion *"被关注 issue 到达审批点 / run 失败时该 Agent 被启动"* spans
T-002 (add the watch) and T-003 (act on it). T-003's spec tests seed entries directly via
`WatchEntryStore`, which is acceptable per `design/testing.md` (no real network/grain). Just
confirm T-003's seeded tests are sufficient to prove the dispatch behavior end-to-end at the
spec level; no new task needed.

## Coverage check — issue acceptance criteria → tasks

| Acceptance criterion | Covered by | Status |
|---|---|---|
| `watch add` → `issue view` / `watch list` show agent in 关注 | T-002 (projection), T-004 (render) | ✅ |
| Watched issue at approval / run-failed → Agent launched (trigger label = watch) | T-003 (launch + D8 provenance) | ✅ (see F1 for the dedup caveat) |
| `watch remove` stops launch; rule-covered issue shows 静音, others unaffected | T-002 (mute display), T-003 (suppression, issue-scoped) | ✅ |
| Re-`watch add` on a muted issue lifts the mute | T-001 (state machine muted→watching) | ✅ |
| Same event, same Agent via rule + watch → launched once | T-003 (D7 dedup) | ⚠️ see F1 |

## Per-artifact notes

- **proposal.md** — accurate impact map; capabilities (`issue-watch`, `issue-watch-dispatch`)
  match the two spec dirs. No drift.
- **design.md** — decisions are concrete with file:line anchors and real alternatives; the one
  under-specified area is F1. D2's deliberate deviation from the prerequisites mirror
  (WatchEntry is Agent-context-owned, so the route bypasses `IIssueGrain`) is correctly
  justified. D5/D7/D8 are the load-bearing choices and are well-argued.
- **tasks.json** — valid JSON, valid DAG (T-001→{T-002,T-003}, T-002→T-004), every dependency
  points to a strictly lower priority. Split by capability is appropriate; no over-granular
  technical-step tasks. Acceptance criteria include test verification throughout. Only gap is
  F1's criterion needing sharpening in T-003.
- **specs** — the two capability specs are complete and scenario-driven; the tension in F1 is
  between the spec's replay scenario and the design contract's `design/issue-watch.md:63`
  wording ("同一事件里…只启动一次", i.e. within-one-event), which scopes only the collision,
  not cross-delivery replay. Reconciling these is part of F1's resolution.

<promise>FAIL</promise>
