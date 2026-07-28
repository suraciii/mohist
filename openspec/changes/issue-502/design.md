## Context

The proposal identifies drift between `design/eventbus.md` and the implemented dispatcher. `EventDispatcherService` already uses an in-memory per-event/per-handler state map, handwritten exponential backoff from `EventDispatcherOptions`, a durable Orleans reminder, and a per-cycle `blockedSources` set. `EventDispatcherPoke` already offers post-commit, fire-and-forget dispatch requests for WorkflowRun, Issue, and AgentSession producers, but Epic and AgentJob do not use it.

The event dispatcher is a server-owned singleton. Its reminder is the delivery correctness path; producer pokes only reduce latency. The change must keep event schemas, per-source FIFO, at-least-once delivery, retry configuration, and DLQ handling intact.

## Goals / Non-Goals

**Goals:**
- Give all five durable event origins the same post-append best-effort wake-up behavior.
- Keep retry and terminal handler state through a failed settlement write during one process lifetime.
- Export a low-cardinality current blocked-source count for operators.
- Make `design/eventbus.md` accurately state the deployed contract.

**Non-Goals:**
- Add Polly, a broker, dispatcher sharding, or a new persistence model.
- Persist retry attempts across process restarts.
- Change DLQ redelivery APIs, event data, subscriptions, delivery order, or at-least-once semantics.

## Decisions

### Reuse the existing post-commit poke helper

Epic and AgentJob will call `EventDispatcherPoke.PokeAfterCommit` after their event append is durable. Epic will call it after `SaveChangesAsync` only when it staged at least one event. AgentJob will call it only after `IEventStore.AppendAsync` succeeds; an append failure retains its existing recovery obligation and does not poke.

Both grains will receive the existing `IBackgroundTaskLauncher`; their grain factory is used to address the fixed dispatcher grain. Poke failures remain caught and logged by the helper, so no producer command changes outcome.

Alternative: invoke `DispatchNowAsync` inline from each producer. Rejected because it puts handler work and dispatcher availability on the producer write path. Alternative: add a new event-store callback. Rejected because the two existing persistence styles do not share one write boundary and the helper already expresses the required best-effort policy.

### Retain dispatcher state until settlement succeeds

`EventDispatcherService` will remove an event key from `_states` only after `MarkDispatchedAsync` or `IDeadLetterStore.SettleAsync` completes successfully. If settlement throws, the exception remains visible to the caller and the state stays in the map. The next cycle therefore retries only settlement: completed handlers are not called again, dead-lettered handlers retain their attempt count, and the source remains undispatched until the transaction succeeds.

The state remains deliberately process-local. A dispatcher restart clears `_states`, allowing the reminder to rediscover the undelivered row and start the retry budget again, as specified.

Alternative: persist retry and handler state with every attempt. Rejected because it adds schema and write-path complexity outside this issue's scope. Alternative: keep the current catch-path removal and document it. Rejected because a transient settlement failure unnecessarily replays completed handlers and discards the in-process retry budget.

### Publish one untagged blocked-source gauge

`EventDispatcherService` will retain the count computed from each completed dispatch cycle and expose it through an observable gauge named `mohist.server.event_dispatcher.blocked_sources`. The gauge will be registered in `RuntimeMetricCatalog` as a unitless, attribute-free instrument. It reports zero for a completed cycle without a pending retry; source values are never metric attributes.

The dispatcher owns the gauge because it alone derives `blockedSources`; no shared tracker or projection is needed. The service will own and dispose its meter with its singleton lifetime.

Alternative: tag a metric with every blocked source. Rejected because source identifiers are unbounded and unsuitable for metric cardinality. Alternative: expose only logs. Rejected because logs do not provide a current, aggregable operator signal.

### Correct the design document alongside code

`design/eventbus.md` will describe the handwritten `EventDispatcherOptions`-controlled backoff, best-effort producer pokes, reminder-backed recovery, process-local restart-reset retry state, FIFO blocking, and the new gauge. It will remove the incorrect Polly and "producers never wake" statements while retaining the existing convergence status once code and tests match.

Alternative: leave the document as a future-state description. Rejected because it is marked converged and is relied upon as the current architecture contract.

### Verification follows existing server test boundaries

Extend dispatcher specs with Epic and AgentJob immediate-delivery coverage under a long reminder period, and retain the lost-poke reminder-recovery scenario. Add a deterministic service/spec test where dead-letter settlement fails, then succeeds, proving the completed/dead-lettered handler state is reused. Extend `RuntimeMetricCatalogTests` and use a `MeterListener` to assert the blocked-source gauge reports nonzero and returns to zero without source tags. Run the server build and full server test suite.

## Risks / Trade-offs

- [Immediate pokes change Epic and AgentJob timing] -> Verify delivery through the existing long-reminder fixture; handler invocation remains exclusively in the dispatcher.
- [Keeping `_states` after a settlement outage retains memory] -> Entries are removed on successful settlement and are bounded by the dispatcher batch and active unresolved events; a process restart clears them.
- [Retry state still resets after restart] -> State this limitation explicitly in the design contract; persistence is deferred to a separate change.
- [Observable gauges can be misleading during an active cycle] -> Publish the last completed cycle's count and omit source tags, making the metric stable and low-cardinality.

## Migration Plan

1. Deploy the server binary with the new producer pokes, settlement-state handling, metric, and documentation.
2. No migration, backfill, configuration change, or dependency update is required. Existing undelivered rows remain discoverable by the reminder; newly appended Epic and AgentJob events receive the lower-latency wake-up path.
3. Verify the blocked-source gauge is registered and run the server build plus full server tests before rollout.
4. Roll back by redeploying the previous server binary. No durable state is introduced, and reminder-driven delivery continues; only the new low-latency pokes, gauge, and within-process settlement retention are removed.

## Open Questions

None. Retry-attempt persistence, DLQ UI/API changes, and dispatcher sharding remain intentionally out of scope.
