## Context

The shared React session page renders `Waiting for activity...` for every running empty transcript and `No activity recorded for this session` for every terminal one. That view has two distinct causes: no runtime content was persisted, or an explicit `?rt=` runtime filter excluded content that exists in the same logical AgentSession. Workflow metadata already exposes a total persisted-part count, while transcript responses expose turns and each turn's runtime session ID; generic sessions can obtain the same comparison through the existing unfiltered transcript read.

Realtime transcript events are delivered over the best-effort `OnTranscriptEvent` channel. `useSessionTranscript` currently requires exact logical session ID, runtime session ID, and runtime equality. This correctly protects historical runtime views but drops all events while page metadata has not yet resolved a physical runtime ID. Workflow pages also substitute the route session name for the canonical logical session ID during that gap.

The Session domain remains the authority for persisted transcript data and physical runtime binding. The Web is only interpreting read data and best-effort push events; server-side binding validation and transcript filtering remain unchanged.

## Goals / Non-Goals

**Goals:**

- Derive a specific empty-state cause from existing session and transcript evidence.
- Provide a same-session link to a historical runtime that contains visible content.
- Accept live events during temporary physical-binding metadata gaps only when their session identity is proven.
- Preserve logical-session isolation, stale-runtime rejection, and the immutability of historical `?rt=` views.

**Non-Goals:**

- Build a general diagnostic panel, retry missing runner uploads, or change persisted transcript content.
- Change runtime-event HTTP routes, server binding validation, or runtime-filtering semantics.
- Make project, issue, or session name a replacement for canonical AgentSession identity when a contradictory canonical identity is present.
- Backfill historical sessions or alter the lineage model.

## Decisions

### Derive empty-state evidence with a conditional unfiltered transcript read

The two session data sources will expose a compact empty-state input to `SessionDetailShell`: the selected runtime ID, whether it was explicitly selected by `?rt=`, and the runtime IDs with visible content in the same logical session.

For an explicit runtime view that returns no visible turns, the data source will issue the existing transcript read without `runtimeSessionId`. It will derive candidate history from turns that contain visible assistant content and whose user runtime ID differs from the selected runtime, retaining only IDs present in session lineage. The read is disabled while the selected transcript has visible content. The normal unfiltered view remains the evidence for generic and unanchored session pages; a zero result means no uploaded content.

`SessionDetailShell` will select one of three presentation states: running/no uploaded content, terminal/no uploaded content, or runtime-filtered empty. The runtime-filtered state receives a history target built through the existing lineage-path builder and is rendered as an ordinary link, not a new diagnostic surface.

Alternative considered: add total part counts and per-runtime counts to all summary DTOs. This would avoid the conditional read but expands API projections and still cannot identify which historical runtime is actionable without additional aggregation. Reusing the transcript endpoint keeps the server contract and persistence model unchanged.

### Keep identity matching strict, then relax only a missing physical anchor

`useSessionTranscript` will receive canonical logical-session identity separately from route lookup identity, plus a `isHistoricalRuntimeView` flag derived directly from the presence of `?rt=`. The issue data source will prefer `detail.id`, then the matching session-list item's `id`; it will never pass `sessionName` as `sessionId`.

The event matcher will apply this precedence:

1. A nonempty event `sessionId` must equal the canonical visible session ID. Any different value rejects the event.
2. When both page and event physical runtime IDs are known, they must match. When both runtimes are known, they must match as well.
3. An unanchored current view with a missing physical ID on either side may accept an event after a proven logical match.
4. An explicit `?rt=` view never uses logical fallback, even before metadata resolves.
5. Missing or ambiguous identity rejects the event.

Generic session pages will subscribe using the route's canonical session ID while their summary query is loading, then stop or continue according to the resolved running status. This closes the metadata-loading gap without treating an unknown page as a live session indefinitely.

Alternative considered: remove physical runtime matching after canonical session matching. This would be smaller but would append live events from a replacement runtime to a historical transcript and violate the runtime-lineage contract.

### Publish workflow lookup context only on the best-effort transcript envelope

The server will extend the internal `TranscriptEnvelope` with optional workflow-origin fields: `projectId`, `issueNumber`, `workflowRunId`, and `sessionName`. `AgentSessionGrain.FanOutRealtimeAsync` will populate them from workflow-session labels; generic-session envelopes leave them absent. The Web event normalizer and `AgentDetailEventMap` will preserve these optional fields.

This permits the spec-required fallback when a workflow realtime event lacks canonical `sessionId`: an active, unanchored workflow page may accept it only when all four origin fields match its resolved context. A present but conflicting canonical ID or physical runtime ID still wins and rejects the event. No origin fallback is available to generic sessions because they already have a stable route `sessionId` and do not have workflow identity.

Alternative considered: rely on `sessionName` or `(projectId, issueNumber)` alone. Neither is unique across workflow runs. Alternative considered: add this context to durable runtime events or server filtering. Both change the authority/persistence surface unnecessarily; the new fields are only ephemeral UI-routing metadata on the existing best-effort channel.

### Keep diagnosis and event matching in shared Web boundaries

Data-source hooks own acquisition and derivation of session evidence. `SessionDetailShell` owns presentation choice. `useSessionTranscript` owns the one identity-matching predicate used by every realtime event handler. This avoids duplicating cause classification across issue and generic pages or allowing individual event handlers to diverge.

Focused Web tests will cover the derivation and UI states, `useSessionTranscript` identity matrix, both data-source identity wiring paths, and historical view isolation. Server specs will cover optional envelope origin population and ensure generic envelopes do not fabricate workflow fields.

Alternative considered: place all logic in `SessionDetailShell`. That would force the view to issue queries and understand session-source differences, and would duplicate the realtime identity rules at rendering call sites.

## Risks / Trade-offs

- [The conditional unfiltered read briefly adds one request when an explicit runtime view is empty] -> Enable it only after the filtered response is available and empty; cache it under the existing session transcript query key with a null runtime selector.
- [A persisted turn can exist without user-visible assistant content] -> Treat only turns with visible transcript content as evidence for a history-switch action; a non-visible part must not produce a misleading historical-content diagnosis.
- [Best-effort realtime metadata can still be incomplete] -> Require the strongest available identity, reject ambiguity, and rely on normal query refetch for eventual reconciliation.
- [Adding workflow origin fields exposes more routing metadata to subscribed Web clients] -> Limit fields to existing session labels, omit them for generic sessions, and keep them off durable APIs and domain-event channels.
- [A data-source regression could reintroduce route-name-as-ID matching] -> Add a test where workflow session name differs from canonical AgentSession ID and assert realtime events still match the canonical ID.

## Migration Plan

1. Extend the transient transcript envelope and Web normalization/types, with server and Web contract tests for optional workflow origin fields.
2. Add the shared matcher inputs and precedence tests, then wire canonical IDs and historical-view flags from both session data sources.
3. Add conditional unfiltered evidence derivation and the empty-state/history-link presentation, with no API or schema migration.
4. Run focused Web typecheck/tests and server specs, then deploy as a backward-compatible Web/server pair. Older servers remain functional because canonical-session matching still works; origin fallback is simply unavailable until both sides are deployed.

Rollback consists of reverting the Web matcher and presentation changes. The added envelope fields are optional and ignored by older clients; no persisted data, endpoint, or migration requires rollback.

## Open Questions

- None. The history action will use the first lineage-ordered runtime with visible content; richer runtime-content summaries remain outside this change.
