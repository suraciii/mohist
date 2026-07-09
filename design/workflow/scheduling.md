# Workflow Scheduling

Level-triggered reconciliation. Scheduler keeps no memory: every decision is a stateless query over persisted state, reconciled by the next poll.

## Model

```
WorkflowGrain / WorkflowRun          ★ single dispatch ledger
  owns Assignment + work lifecycle (Pending/Running/terminal)
  ClaimNext: atomic Pending→Running + stage lock
  consumes reports; idempotent (terminal work re-report → Stale)
  no timer, no runner concept

AgentJobGrain
  owns work state + DispatchSnapshot (no run to rerender from)

RunnerGrain
  presence: lastSeen — poll is heartbeat → online/offline
  slots: capacity config (control-plane owned)
  closeout: presence loss → synthesize FAILED for Running works
  holds NO work records

DispatchService (stateless, not a grain)
  per poll: desired − reported → dispatches
  from persisted state; no cursor, no cache, no ledger

runner process (physical)
  executes works concurrently; progress-aware timeout
  reports full state each poll: inFlight + awaitingAck
  retries reports with backoff until acked
```

Every fact has one owner:

```
who was dispatched what             → WorkflowRun / AgentJob (store-queryable)
what is executing right now         → runner process memory (reported each poll)
is the runner alive                 → RunnerGrain.lastSeen
```

No third copy. Dispatch is always re-renderable from the persisted run.

Invariants:

```
the workflow run IS the dispatch ledger
Running ⟹ reconciled within one poll: reported ∨ re-dispatched ∨ closed out
|Running works assigned to runner| ≤ slots  (enforced at claim)
```

## Poll reconciliation

```
runner process                  DispatchService                      store/grains
    | POST poll {inFlight, awaitingAck}                                  |
    |------------------------------>|                                    |
    |                               | ① TouchPresence (poll=heartbeat)   |
    |                               | ② desired ← Running WHERE assigned=me
    |                               | ③ redelivery = desired − reported  |
    |                               |    render each from persisted run  |
    |                               | ④ spare = slots − |desired|        |
    |                               |    while spare > 0:                |
    |                               |      Ready runs assigned to me     |
    |                               |      ORDER BY ReadySince ASC       |
    |                               |      ClaimNextAsync ---------------->| Pending→Running
    |                               |        ok → render, spare--        |   + stage lock
    |                               |        null → next candidate       |
    |                               |    still spare: claimable Pending  |
    |                               |      → AssignWorker → ClaimNext    |
    | { dispatches[] }              |                                    |
    |<------------------------------|                                    |
    | inFlight.add(dispatches)      |                                    |
    | execute concurrently           |                                    |
```

Order: redelivery first (debts owed) → assigned Ready runs → claim new. Held work before expansion.

`reported − desired` (run stopped past the work): no action. Process runs to completion. Report answers `Stale` = ack, result discarded.

Race freedom: process adds work to inFlight synchronously between receiving dispatch and next poll. A freshly delivered dispatch can never be mistaken for loss.

Reported set (`inFlight ∪ awaitingAck`) is process-lifetime state. Must survive poll exceptions and connection resets. Otherwise transient poll failure = every held work vanishes from report = re-dispatch storm.

## Claim

`ClaimNextAsync`: picks next pending work, acquires stage lock, marks Running with runner identity, persists. One atomic write. No offer phase. No runner-side pre-registration.

```
PENDING --ClaimNext--> RUNNING --report(success|fail)--> COMPLETED|FAILED
```

Failed claim (stage lock contention, state moved) → null → next candidate this poll.
Successful claim with lost dispatch → work is Running and unreported → next poll redelivers.

## Fairness

`ReadySince` timestamp on (re-)entry to Ready. Serve `ORDER BY ReadySince ASC` = round-robin with zero scheduler state.

```
work completes → run advances → next work pending → ReadySince := now
just-served runs re-queue at tail; longest-waiting at head
```

Pluggable policy point: default pure FIFO, can extend to `Priority DESC, ReadySince ASC`.

## Capacity

`slots` bounds concurrently executing workflow works, not held assignments. Gate evaluated at claim time from store (`|Running assigned to me| < slots`). Process enforces nothing.

## Report

Reports flow directly to owning grain. Stateless translation service. No relay.

```
runner → api route → translate (stateless) → owner grain → Accepted | Stale (both ack)
```

At-least-once: finished work → `awaitingAck` → retry original result with backoff → still in poll report → never mistaken for lost. `Accepted` and `Stale` both terminate retry.

Report producers are indistinguishable to the owner: executing process (normal or timeout failure) or RunnerGrain closeout.

## Supervision

| What | Who | How |
|---|---|---|
| work wedged/runaway | runner process | progress-aware timeout → kill, report FAILED |
| runner gone | RunnerGrain | poll-freshness expiry → offline → closeout: synthesize FAILED("runner-lost") for Running works |
| work timeout | none | work reported in-flight is alive; only process judges slow |

HTTP heartbeat = info-refresh channel only. Poll freshness = presence signal. Registry written only on state/info change, never per poll.

## Failure handling

| Failure | Handling |
|---|---|
| dispatch response lost | next poll: desired − reported → re-dispatch |
| process restart | empty report → full re-dispatch |
| render fails after claim | retried every poll |
| report transport fails | awaitingAck retry; still reported, never re-dispatched |
| duplicate/late report | owner idempotent → Stale |
| work wedged | process timeout → FAILED |
| runner lost | closeout synthesizes FAILED |
| runner returns after closeout | reports answer Stale; works no longer desired, drain |
| run stopped while work executing | no cancellation; report answers Stale |
