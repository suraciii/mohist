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
3. An unanchored current view with no page physical runtime ID may accept an event after a proven logical match. If the page has a physical runtime ID, an event that omits one is rejected.
4. An explicit `?rt=` view never uses logical fallback, even before metadata resolves.
5. Missing or ambiguous identity rejects the event.

Generic session pages will subscribe using the route's canonical session ID while their summary query is loading, then stop or continue according to the resolved running status. This closes the metadata-loading gap without treating an unknown page as a live session indefinitely.

Alternative considered: remove physical runtime matching after canonical session matching. This would be smaller but would append live events from a replacement runtime to a historical transcript and violate the runtime-lineage contract.

### Retain the session-scoped realtime envelope

`TranscriptEnvelope` already carries the required canonical `sessionId`, and server fan-out supplies it from the AgentSession. The Web will use that canonical identity for the missing-page-binding fallback; it will not add workflow, project, issue, or session-name fields to the best-effort transcript envelope. A realtime event without a canonical session ID is ambiguous and is rejected.

Alternative considered: enrich the transcript envelope with workflow lookup context to match a missing session ID. This would expose workflow metadata on a channel intentionally scoped to sessions, while no current producer omits the canonical ID. Alternative considered: use `sessionName` or `(projectId, issueNumber)` alone. Those values are not canonical session identity and are insufficient to safely override a missing session ID.

### Keep diagnosis and event matching in shared Web boundaries

Data-source hooks own acquisition and derivation of session evidence. `SessionDetailShell` owns presentation choice. `useSessionTranscript` owns the one identity-matching predicate used by every realtime event handler. This avoids duplicating cause classification across issue and generic pages or allowing individual event handlers to diverge.

Focused Web tests will cover the derivation and UI states, `useSessionTranscript` identity matrix, both data-source identity wiring paths, and historical view isolation. The identity matrix includes rejecting an event whose physical runtime ID is absent after the page has resolved one.

Alternative considered: place all logic in `SessionDetailShell`. That would force the view to issue queries and understand session-source differences, and would duplicate the realtime identity rules at rendering call sites.

## Risks / Trade-offs

- [The conditional unfiltered read briefly adds one request when an explicit runtime view is empty] -> Enable it only after the filtered response is available and empty; cache it under the existing session transcript query key with a null runtime selector.
- [A persisted turn can exist without user-visible assistant content] -> Treat only turns with visible transcript content as evidence for a history-switch action; a non-visible part must not produce a misleading historical-content diagnosis.
- [Best-effort realtime metadata can still be incomplete] -> Require the strongest available identity, reject ambiguity, and rely on normal query refetch for eventual reconciliation.
- [A late event can omit its physical runtime ID after the page has resolved a binding] -> Reject it rather than using logical fallback, so a replaced runtime cannot contaminate the current view.
- [A data-source regression could reintroduce route-name-as-ID matching] -> Add a test where workflow session name differs from canonical AgentSession ID and assert realtime events still match the canonical ID.

## Migration Plan

1. Add the shared matcher inputs and precedence tests, then wire canonical IDs and historical-view flags from both session data sources.
2. Add conditional unfiltered evidence derivation and the empty-state/history-link presentation, with no API or schema migration.
3. Run focused Web typecheck/tests, then deploy as a Web-only backward-compatible change. The existing session-scoped realtime envelope already supplies canonical session identity.

Rollback consists of reverting the Web matcher and presentation changes. No persisted data, endpoint, envelope, or migration requires rollback.

## Open Questions

- None. The history action will use the first lineage-ordered runtime with visible content; richer runtime-content summaries remain outside this change.
