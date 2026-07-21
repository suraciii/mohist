## Context

`AgentSessionGrain.AppendEventsAsync` currently returns immediately when a non-empty runtime-event batch has a missing or stale `runtimeSessionId`. This protects the logical AgentSession's current physical binding, but the rejection produces no diagnostic. After binding validation, the grain materializes event envelopes and passes them to `TranscriptAccumulator` and realtime fan-out; both surfaces use `TranscriptAccumulator.EventTypes` as the shared allowlist and silently skip unsupported types.

The Session context owns runtime binding and transcript facts. The proposal and `agent-session-event-discard-observability` spec require warning-level evidence at these two discard boundaries without changing event acceptance, state transitions, returned event information, transcript persistence, or realtime publication. The grain already owns an `ILogger<AgentSessionGrain>`, and its in-process spec fixture already provides a recording logger, fake stores, and fake time.

## Goals / Non-Goals

**Goals:**

- Log every non-empty batch rejected by current-binding validation with logical session identity, expected and reported physical identities, and discarded count.
- Log unsupported transcript event types with logical session identity, exact type, and per-type discarded count.
- Preserve the current binding guard, allowlist authority, processing order, persistence results, and realtime publication results.
- Cover both diagnostics and unchanged discard behavior at the Session grain boundary.

**Non-Goals:**

- Change the runtime-events API, response shape, allowlist, Session state model, or database schema.
- Add retries, buffering, metrics, alerts, sampling, rate limiting, or payload logging.
- Refactor transcript accumulation or realtime publication beyond the observability addition.

## Decisions

### 1. Log binding rejection at the existing grain guard

Immediately before the current binding-mismatch return, `AppendEventsAsync` will emit one structured warning. The template will carry `SessionId`, `ExpectedRuntimeSessionId`, `ReportedRuntimeSessionId`, and `DiscardedEventCount`; nullable structured values represent an absent current or reported binding. The existing early return remains in place, so no state, accumulator, publisher, timer, or retry path is entered.

Alternative considered: log in each HTTP route. Rejected because runtime-binding authority is the grain, multiple routes call it, and route logging would duplicate validation knowledge while missing direct grain callers.

### 2. Derive unsupported-type diagnostics from the existing allowlist

After `AppendEventsAsync` has built `allEntries`, it will group entries whose type is absent from `TranscriptAccumulator.EventTypes`, using ordinal type equality, and emit one structured warning per unsupported type. Each warning will carry `SessionId`, `EventType`, and `DiscardedEventCount`. Logging occurs before accumulator acceptance and fan-out, but `allEntries` is not filtered or rewritten: the existing accumulator and publisher checks remain responsible for their own skips.

This placement preserves less obvious current behavior, including envelope sequencing, returned input-event information, and any non-transcript Session processing that occurs before allowlist filtering. It also keeps `TranscriptAccumulator.EventTypes` as the only allowlist authority.

Alternatives considered: inject a logger into `TranscriptAccumulator`, rejected because it would observe only persistence and add a dependency to a focused accumulator; log independently inside the accumulator and fan-out skip branches, rejected because one unsupported event would produce duplicate warnings; remove unsupported events before envelope processing, rejected because it would change behavior beyond observability.

### 3. Verify behavior in focused grain specs

Add `AgentSessionEventDiscardObservabilitySpecs` using the existing in-process `AgentSessionGrainFixture`. Binding cases will cover stale and missing reported identities, assert warning level and structured values, and compare state/store/publisher effects before and after the rejected batch. Unsupported-type coverage will submit a mixed batch with repeated unsupported events, assert one warning with the per-type count, flush deterministically, and verify unsupported entries are absent while supported entries retain existing persistence and publication behavior. A supported-only case will assert no discard warning.

The fixture's transcript publisher will become a resettable recording fake so publication behavior is directly observable. Its test logger will retain structured state fields in addition to the formatted message, allowing assertions on named values rather than text formatting. Tests use the existing fake clock and in-memory stores; no API-level duplicate matrix, real time, network, or external dependency is needed.

Alternative considered: extend the existing recovery API spec only. Rejected because it covers one stale-binding route but cannot efficiently exercise missing identity, per-type aggregation, or publisher behavior; the grain is the lowest boundary owning the complete behavior.

## Risks / Trade-offs

- `[A noisy producer repeatedly sends unsupported types or stale batches] ->` Aggregate unsupported events once per type per batch and binding failures once per batch; do not log payloads or individual events.
- `[Logging accidentally changes filtering order or event results] ->` Add diagnostics around the existing guard and materialized entry list without removing, reordering, or rewriting entries; retain regression assertions for state, persistence, return, and publication behavior.
- `[The persistence and publication allowlists diverge later] ->` Continue referencing the existing shared `TranscriptAccumulator.EventTypes` set and keep the warning at their common grain orchestration boundary.
- `[Runtime session identifiers appear in logs] ->` Log only logical/physical identifiers and counts required for diagnosis; never include event payloads, prompts, or transcript content.

## Migration Plan

1. Add focused grain specs and recording support for structured logs and transcript publication.
2. Add the binding-rejection warning at the existing early-return guard.
3. Add per-type unsupported-event warnings from `allEntries` using the existing allowlist.
4. Run the focused Session specs, then the required server test suite.
5. Deploy as a normal server update. No migration, backfill, configuration change, or coordinated Runner/Web/CLI rollout is required.

Rollback is a server-code revert. It restores silent discard behavior; no persisted data or external contract requires rollback handling.

## Open Questions

None. Warning level, diagnostic fields, aggregation unit, and unchanged discard semantics are fixed by the proposal and capability spec.
