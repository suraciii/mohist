# Self Review: Issue 522 Plan (round 2)

## Findings

### 1. [Blocking] D8's activity-driven terminal clobbers the launch Turn's authoritative terminal result

Round-1 finding 1 was addressed by adding D8, which drives non-launch Turn status from runtime facts. D8's `Queued → Executing` promotion is safe for the launch Turn (issue-512 removed its Runner `session.input`, so it never fires). But D8's **terminal** transition is not safe for the launch Turn, contrary to D8's claim that "the launch Turn is unaffected."

`AppendTerminalCloseAsync` — the AgentJob's terminal delivery for the launch Turn — builds a `session.activity` payload with `status: completed|failed` and ingests it through the **same** `AppendEventsAsync` → `ApplyRuntimeEventToDomain` path used by Runner runtime events (`AgentSessionGrain.cs:819-836`). Under D8 that terminal `session.activity` would mark the current Turn terminal.

Ordering makes it a regression. `AgentJobGrain.EnterTerminalStateAsync` awaits `DeliverTerminalToSessionAsync` (→ `AppendTerminalCloseAsync`) at `AgentJobGrain.cs:1323` **before** `MarkInitialTurnTerminalAsync` at `:1324`. So under D8 the thin activity-driven terminal lands first; the authoritative `MarkInitialTurnTerminal` — carrying the Turn's `message`/`output`/`failureCategory`/`exitCode` — then no-ops (terminal guard, `Transitions.cs:492`). The launch Turn's rich terminal result is lost.

Recommended fix (for the fix task): guard D8's activity-driven terminal so it does not apply to a launch Turn (a Turn whose `JobId` is set), or skip `session.activity` facts that carry the AppendTerminalClose markers (`agentJobId`/`deliveryId`). The launch Turn's terminal must remain solely AgentJob-driven. D8's "launch Turn is unaffected" must be reworded to cover the terminal path, and T-001's "launch-Turn isolation" criterion must explicitly test the terminal-close path, not only the `session.input` promotion.

### 2. [Resolved] Prior round-1 findings verified fixed

- Round-1 finding 1 (follow-up Turn lifecycle unspecified): resolved by D8 + T-001 lifecycle criteria + T-002/T-003 notes — modulo the blocking edge in finding 1 above.
- Round-1 finding 2 (D5 listed a non-existent `Stopped` Turn status): resolved; D5 now enumerates `Completed|Failed|Cancelled|Unknown` and clarifies `stopped` is a reply label only.
- Round-1 finding 3 (command-surface docs): resolved; T-004 now requires updating `design/cli.md` and `docs/cli-reference.md`.

### 3. [Observation, non-blocking] Follow-up cancel is best-effort by design

D8 documents that follow-up cancel cannot un-deliver a synchronously delivered input (launch-Turn cancel stays deterministic). A related consequence is not spelled out: after a follow-up-Turn cancel the Server marks activity `idle`, so a new follow-up may be accepted and dispatched while the runtime still has the cancelled input — the runtime serializes inputs so there is no true concurrency, but the Server/runtime views diverge briefly. This is within the accepted follow-up-cancel tradeoff and does not block build; noting it so the implementer is aware.

## Verdict

Finding 1 is a build-blocking correctness regression introduced by the D8 fix: it would silently drop the launch Turn's authoritative terminal result. The plan is not ready to build until D8's terminal path is guarded against launch Turns.

<promise>FAIL</promise>
