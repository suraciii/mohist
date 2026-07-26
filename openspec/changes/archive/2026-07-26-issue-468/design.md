## Context

The proposal identifies `/api/agent/activity` as a polling path whose card `eventSummary` is currently rebuilt from `AgentSessionTranscriptTurns` and `AgentSessionTranscriptParts` for every request. `AgentActivityFeedAssembler` loads the same parts once for the latest-activity preview and again for `TranscriptReductions`; the latter is avoidable read-side work.

An AgentSession already owns the accepted Runtime observations, its activity, usage, and durable JSON state. `AgentSessionGrain.AppendEventsAsync` serializes accepted observations and `AgentSessionStore.SaveAsync` persists Session state. Transcript persistence is deferred and retried independently, so the summary must be derived at the Session write boundary rather than coupled to a successful transcript flush. This preserves the Session context as the owner of execution observations and leaves AgentOps as a read-only assembler.

## Goals / Non-Goals

**Goals:**
- Persist an incremental, restart-safe event summary in AgentSession state when accepted Runtime observations change it.
- Preserve the existing event-summary semantics: most recently resolved model, final tool-part state within each turn, monotonic failed-tool history across sealed turns, and a failure reason/category pair from the latest `session.activity` part.
- Make activity-card `eventSummary` a projection of the persisted Session summary, without changing the activity response schema or its route aliases.
- Remove transcript materialization performed solely for activity-card summaries and keep amplification counters aligned with work actually done.

**Non-Goals:**
- Do not alter transcript detail endpoints, transcript retention, or the existing latest-activity preview projection.
- Do not change activity-card selection, reconciliation, ordering, limits, workflow progress, waiting cards, or usage projections.
- Do not backfill historical Session JSON or introduce a compatibility fallback that rebuilds absent summaries during activity polling. This project does not require persisted-data compatibility; Sessions created or updated after deployment own the new summary.
- Do not convert other Session read surfaces that independently need transcript metadata as part of this change.

## Decisions

### Keep the summary inside AgentSession state

Add a private persisted activity-summary state to `AgentSession`. It contains the public `AgentSessionTranscriptSummary` values plus the minimum reducer state needed to update them correctly after a grain restart: sealed-turn tool and failed-tool identifier sets, the current turn's final normalized part state and ordering, and the latest `session.activity` part candidate. The public summary keeps `resolvedModel`, `failureCategory`, `failureReason`, `toolCallCount`, and `toolErrorCount`; card DTO mapping continues to use `AgentSessionDtoMapper`.

The reducer consumes the same normalized part mutations and correlation keys that `TranscriptAccumulator` uses for persistence, rather than raw Runtime events. Within the open turn, a later mutation replaces that part's payload; when `session.input` seals the turn, its final tool parts are merged into the durable seen-tool and failed-tool sets. A failure recorded in a sealed turn is never removed by a later turn, while a failed observation followed by a completed final part in the same open turn is not counted as an error. The reducer uses the projector's identifier extraction and its per-part fallback rule.

Every `session.activity` part, not only a terminal one, is a failure-pair candidate. The reducer stores the candidate with the same `(turn sequence, part sequence, part identifier)` order as the normalized transcript part stream; the latest candidate supplies both failure fields, including clearing both when its payload has neither value. This preserves the current projector's behavior and prevents a category from one fact being paired with a reason from another.

Alternative considered: persist only the five displayed fields. Rejected because it cannot distinguish repeated tool observations, final same-turn state, and sealed historical failures after restart. Alternative considered: add a summary table and tool-call child table. Rejected because it creates a second persistence model and transaction boundary for a compact Session-owned projection; the existing `State` JSON is already the durable aggregate representation.

### Update the summary on accepted observations

`AgentSessionGrain.AppendEventsAsync` obtains the normalized mutations from `TranscriptAccumulator`, applies them to the summary state, and seals the prior turn through the same input boundary that flushes its transcript data before marking Session state dirty. The resulting summary is saved by the existing Session state-and-domain-event transaction. Rejected or stale-binding observations do not change it. The summary therefore represents accepted Session facts even if deferred transcript persistence must retry later.

Alternative considered: recalculate the summary from transcript rows after every flush. Rejected because transcript and Session state currently commit through independent stores, and a flush retry would make the summary stale or require another cross-store coordination path.

### Replace only the activity feed's summary reduction

`AgentActivityFeedAssembler` passes the persisted summary from each `AgentSessionRecord` to `ToActivityCard` and removes its `TranscriptReductions.LoadEventSummariesWithCountAsync` call. It retains the existing latest-event loader for `lastActivity`, issue-title lookup, reconciliation, and workflow progress. `transcriptRecords` consequently reports the records used by the preview loader only; it no longer includes a second full part load for card summaries.

Alternative considered: make the activity feed replay transcripts once and share that result between preview and summary. Rejected because polling work would still grow with transcript history and would not satisfy the persisted-summary requirement.

### Treat absent persisted summaries as empty

The new state member defaults to an empty summary when deserializing an existing or incomplete Session document. No EF migration is needed because the member is serialized inside the existing `AgentSessions.State` JSON. The activity read path never falls back to transcript reduction for an absent summary.

Alternative considered: lazy backfill on the first activity read. Rejected because it restores the history-dependent polling cost and makes a read mutate persistent state.

### Verify behavior at reducer and API boundaries

Add focused Session unit tests for model replacement; same-turn tool failure followed by completion; a sealed failed tool followed by completion in a later turn; session-activity candidate ordering and pair clearing; persistence/reload; and rejected stale events. Add or update Session/AgentOps specs for activity-card output and canonical/alias parity. Extend the existing amplification specs with controlled transcript populations and operation counters, asserting that summary construction adds no transcript records. Tests use the existing fake clock and in-memory SQLite; they do not use wall-clock measurements.

## Risks / Trade-offs

- [The persisted summary can lead deferred transcript evidence after a transcript-store failure.] -> The summary represents accepted Session observations by design; retry retains the transcript accumulator, and activity preview remains independently best-effort evidence.
- [Per-tool reducer state grows with distinct tool calls.] -> Retain only sealed identifier sets and the current turn's final part metadata, never tool payloads or output; transcript remains the detailed audit record.
- [An event mapping drift can make the reducer differ from transcript semantics.] -> Centralize normalized-part reduction in one pure reducer and cover the existing model/tool/session-activity ordering matrix with unit tests.
- [Pre-deployment Sessions have no stored summary.] -> Normalize missing state to empty and do not add polling fallback; this is an explicit no-compatibility decision for the actively developed project.
- [Preview reads still scale with transcript parts.] -> This change removes only the duplicate event-summary reduction; selecting a bounded latest preview is separate work and remains observable through `transcriptRecords`.

## Migration Plan

1. Add the persisted summary state and pure incremental reducer, with deserialization defaulting missing state to empty.
2. Update the grain to reduce accepted observations before its existing state save; preserve stale-binding rejection and deferred transcript retries.
3. Change the activity assembler to consume the stored summary and remove the batch summary transcript load, then update amplification accounting.
4. Add reducer, persistence/reload, activity-route, and operation-count coverage.
5. Deploy without an EF migration or data backfill. New and updated Sessions write the summary on their next accepted observation.

Rollback consists of reverting the reader to `TranscriptReductions` and the writer changes. The added JSON member is ignored by prior code, so no database rollback is required.

## Open Questions

None. The design deliberately leaves preview-query bounding and historical summary backfill outside this change.
