# Self Review — Issue 567 (Runner 更新应中断并恢复活跃 Agent 工作)

Re-review (round 2): the previous round FAILED on one must-fix (MF-1, the
missing Server→Runner channel for the update-operation identity) plus six
observations. Artifacts were revised in `0cd3dd634`, `c43a0a7b6`, `10553398e`;
this round verifies those dispositions and checks the fixes for regressions
rather than re-sweeping from scratch. Every claim added by the fix was
verified against live source (details below).

## Verdict

**PASS** — MF-1 is fixed properly, the observations' dispositions hold, and
the fix introduced no regressions. The plan is ready to build.

## Disposition verification

### MF-1 (must-fix): Server→Runner channel for the update-operation identity — FIXED

The fix specifies the full handoff the previous round demanded, consistently
across all artifacts:

- **Channel.** A runner-authenticated, read-only pending-operation query
  (`GET /api/runner/{id}/update-operation/pending`) owned by T-001 (description,
  AC2, output, notes), consumed by T-003 at shutdown. The endpoint follows the
  existing authenticated runner-GET pattern (`/config`,
  `RunnerRoutes.cs:196`); nothing like it exists today, matching the gap.
- **Payload.** Operation id, creation time, and the **affected-work
  inventory** — the richer payload the previous round suggested — resolving
  Obs-4 as a side effect.
- **No-operation-known behavior.** Ordinary restart, unreachable Server
  (e.g. a `full` update's just-restarted Server despite in-budget retries),
  or expired handoff budget ⇒ no receipts, `started` fences stand, honest
  unresolved. Stated identically in the new spec requirement, D3, the new
  risk row, recovery.md's two new failure-rule rows, and T-003's ACs.
- **Wrong topology statement removed.** T-003's notes now assert the
  CLI's `/update-interrupt` confirmation response "is never a party to the
  Runner process" — verified in source that `RunnerRefreshOutcome.cs:175` is
  its sole caller and no runner-side channel carries update knowledge. The
  only two remaining "confirmation response" mentions (design context,
  T-003 notes) both state the correct CLI-only topology; nothing relies on
  the response reaching the Runner.
- **Specified behavior, not just mechanism.** New spec requirement "The
  Runner learns the update-interrupt fact at shutdown" with four scenarios
  (fetch / ordinary restart / unreachable / inventory-limits); the
  prompt-stop requirement gained the bounded-handoff clause plus a
  "shutdown handoff is bounded" scenario.
- **Design rationale recorded.** D3 explains why a shutdown-time fetch beats
  a SignalR push at fence creation (losable, needs a fetch fallback anyway)
  or a poll-response field (missable via the 204-when-idle contract; caches
  a fact a later unrelated restart could act on). The fetch is authoritative
  at the decision moment and distinguishes update-caused from ordinary
  shutdown by construction.

Grounding re-verified against live source: `RunnerUpdateInterruptResponse`
(`RunnerRoutes.cs:98`) is grain-memory-only today with exactly the shape the
plan extends; `WorkResultJournal` has only `started`/`completed`
(`work-result-journal.ts:7`) so the `interrupted` state is additive; Pi's
`stopConfirmed` (`session.abort()` + `isStreaming` watch,
`pi/runtime.ts:301–327`) exists as described; SignalR dispatchers
(`RunnerSessionStopDelivery`, `RunnerSessionCommandDispatcher`) exist as the
context inventories them.

### Observations 1–6 — dispositions hold

- **Obs-1 (abandoned confirmed interrupt):** no action; the fix additionally
  makes the stale-pending-operation hazard explicit and safe (inventory rule:
  works an operation does not name get no receipts; arbitration remains the
  authority for mismatches). If a stale operation *does* name a held work,
  that work was already durably fenced at confirmation (D1), so a receipt
  merely triggers the already-promised replacement — correct, not harmful.
- **Obs-2 (non-Agent active work):** still out of scope, matching the issue's
  Agent-work framing. Holds.
- **Obs-3 (terminal-result receipt channel ambiguity):** unchanged; harmless
  (both paths at-most-once). Holds.
- **Obs-4 (fence inventory vs runner in-flight set):** resolved by the fix —
  the handoff carries the inventory, and the spec's
  "Receipts are limited to works the operation names" scenario plus T-003's
  AC/test coverage (inventory not naming a held work) pin the rule.
- **Obs-5 (T-002 interim retryable semantics):** unchanged, still explicitly
  interim. Holds.
- **Obs-6 (open questions):** appropriately scoped; the fix added the
  handoff-budget constants to the open-questions list rather than inventing
  values. Holds.

## Regression check on the fix

Adversarially probed the new text; each failure case holds:

- **Prompt-stop regression (AC1)?** The handoff is bounded, is part of the
  bounded shutdown, and "SHALL NOT delay the restart" beyond its bound
  (spec scenario + D3 + T-003 AC3). Total shutdown = handoff budget + stop
  budget, both fixed. The update still never waits for natural turn
  completion. No regression.
- **Race: fetch before the operation exists?** The operation is persisted and
  all markings committed *before* the route returns the confirmation that
  authorizes restart (T-001 AC1/AC6), so the Runner's shutdown fetch always
  lands after durability. No race.
- **`full` update (Server restarts before Runner)?** The operation is durable
  and storage-backed, so the just-restarted Server can answer; the handoff
  budget includes brief retries; expiry degrades to honest-unresolved. Covered
  by an explicit risk row and spec scenario.
- **Chained updates?** "Most recent not-yet-settled" returns the newest
  operation, which fences the replacement identities; D3 states this
  explicitly.
- **TOCTOU between fetch and receipt delivery?** Receipts carry the operation
  id; arbitration re-validates against the durable operation at apply time
  (D5), so a settled-in-between operation is resolved authoritatively.
- **Cross-artifact consistency?** Spec format conforms
  (`### Requirement` / `#### Scenario` WHEN-THEN-AND, matching sibling
  changes); all eight task spec anchors resolve to real requirement headers;
  the new requirement contradicts none of the existing seven in
  `runtime-agent-recovery-receipt` (it supplies the source for the
  operation-id naming that the payload requirement already required).
- **Task graph?** `tasks.json` parses; the graph stays acyclic (T-003 now
  legitimately depends on T-001 for the query and T-002 for the receipt
  port); T-001's added query fits its existing scope and output.

## Pre-existing problems missed in round 1

None found that meet the must-fix bar; the round-1 full sweep's per-dimension
verdicts stand. The one candidate surfaced this round — the undefined
"not-yet-settled" boundary for the pending-operation query — is new text from
the fix (below), not a round-1 miss.

## Observations (non-blocking)

1. **"Not yet fully settled" is not precisely defined** for the
   pending-operation query (does CLI-reported-unresolved work settle an
   operation? does a deadline-expired entry?). Safety properties do not
   depend on the answer (inventory rule + arbitration authority make a stale
   pending operation quiet), but implementation should pin the definition to
   avoid a forever-pending operation causing pointless shutdown fetches.
2. **T-001's spec-test AC list doesn't name the pending-operation query**
   explicitly (AC7 lists fence scenarios); the query behavior is covered
   end-to-end by T-003's handoff test AC (found / absent / unreachable /
   inventory-miss) and AC2 is independently verifiable, but an explicit
   server-side query test line in T-001 would make the split cleaner.
3. Previous round's Obs-1/2/3/5/6 remain open as observations, unchanged in
   substance.

<promise>PASS</promise>
