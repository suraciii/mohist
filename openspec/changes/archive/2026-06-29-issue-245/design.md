## Context

Context exhaustion currently fails silently. The shared `ContextHealthIndicator` (`packages/web/src/widgets/session-health/ui/ContextHealthIndicator.tsx`) only swaps a 6 px dot color (green/yellow/red) and prints a percentage; it carries no explicit alert affordance (no warning glyph, no `aria-live`, only a generic `"Context usage NN%"` tooltip), so on list rows and Pulse cards it reads as decoration rather than a warning. The session-page recovery bar (`SessionPage.tsx:565`) sits inside `SessionHeader` and is not guaranteed to stay visible while the transcript scrolls. Compaction events are rendered per-round inside `SessionTimeline.tsx:303` and are invisible until a round is expanded; on the live session page (`SessionTranscriptLayout`) they are not surfaced at all. The runtime-session lineage produced by compact/reset is **not persisted**: `RebindRuntimeSession` (`AgentSession.Transitions.cs:99-113`) captures the old `AgentRuntimeSessionId` into a local only to emit `AgentSessionRuntimeBound(newId)` and then discards it — the predecessor is irrecoverably overwritten, and no DTO exposes a lineage relationship. Finally the activity feed carries only the latest usage snapshot (`activity-cards.ts:93-95`, `AgentSessionQuerier.cs:627-657`); there is no per-session usage history to drive a trend.

Constraints (from `design/architecture.md`, `design/web-ui.md`, `design/domain-analysis.md`): authoritative session state and any new DTO fields belong on the **Server (Agent/Sessions domain)**; the Web is a read-only consumer of the session OHS and submits user intent — it must not become the source of truth for lineage or history. Events are observation-only. Pulse must remain read-only and must not introduce an independent new endpoint (the specs permit only *enriching* the existing live activity feed with a short usage history). Identity rule (`design/conventions.md:28-35`): the stable identity is the Mohist `sessionId`; `AgentRuntimeSessionId` is a mutable runtime facet — so a lineage design keys off the stable session and records the chain of runtime facets. The project is in active development with **no version-compatibility obligation**, so persisted state may be extended freely.

## Goals / Non-Goals

**Goals:**
- Make context-health breaches a proactive, explicit, surface-consistent signal (list rows, Pulse cards, session page) without requiring the user to open a session.
- Keep the recovery bar (context-health bar + Compact/Reset actions) reachable at all times while the transcript scrolls.
- Persist and expose the runtime-session lineage produced by compact/reset, and render a navigable link between predecessor/successor runtime sessions.
- Surface compaction events in a compact summary atop the transcript (no round expansion needed), keeping the per-round detail entry.
- Render a context-usage trend mini-chart on compact cards, backed by a short retained usage history.

**Non-Goals:**
- No server-side auto-compaction trigger or policy engine.
- No cross-session / global health monitoring dashboard.
- No "rounds remaining" estimation.
- No change to Compact/Reset availability on active sessions (the `active || pending` disable in `SessionRecoveryActions.tsx:146,162` stays).
- No distinct per-runtime-session transcript URL / isolated older-runtime-session view (see Open Questions).

## Decisions

### D1. Alert treatment lives entirely in the shared indicator; the classifier is reused unchanged
`classifyContextHealth` / `resolveContextUsage` (`model/context-health.ts`) already implement the canonical 60/80 traffic-light classification and already return `null` for missing data (driving the "hide, don't show empty" behavior). The gap is purely presentational: the alert is too quiet. **Decision:** enhance `ContextHealthIndicator` so that `yellow`/`red` render an *explicit* alert treatment — a warning/error glyph (not just a colored dot), `role="alert"` + `aria-live="polite"` for red (and `role="status"` for yellow), and a *descriptive* tooltip that communicates severity (e.g. `"Context window 82% full — near limit"`) rather than a bare percentage. `green` stays quiet (no alert affordance), and the no-data → hide behavior is unchanged. Because all three surfaces (`SessionCard.tsx:163`, `CompactSessionCard.tsx:90`, session page) already route through this one component, the treatment becomes consistent by construction.
- *Alternatives considered:* (a) add alert logic at each call site — rejected, diverges across surfaces and duplicates the classifier; (b) surface a separate banner per row — rejected, too noisy for a list. Centralizing in the shared widget is the smallest change that satisfies the "consistent across every surface" scenario.

### D2. Make the recovery-bar region sticky within the page scroll context
The left column (`SessionPage.tsx:930-970`) is `flex-col flex-1 min-h-0` with `SessionHeader` (`shrink-0`) above a `flex-1 overflow-y-auto` transcript container; the recovery bar is the last child of the header (`SessionPage.tsx:565-569`). **Decision:** make the recovery-bar sub-region `sticky` (with a background and `z-index` above the transcript) relative to the nearest scroll context, rather than relying solely on the flex-column pin, so it remains reachable independent of viewport height or any outer layout scroll. The context-health bar and the `SessionRecoveryActions` buttons stay together inside that sticky region.
- *Alternatives considered:* (a) sticky-position the entire header — rejected, a tall pinned header steals vertical space on short screens and the breadcrumb/title need not be pinned; (b) a floating Compact/Reset FAB — rejected, diverges from the existing layout and the spec scopes the fix to the recovery bar. Pinning just the recovery sub-region matches the spec ("recovery bar … SHALL remain visible (sticky)").

### D3. Persist a bounded runtime-session lineage chain on `AgentSession`; expose it on the metadata DTO; render a `CompactionLineageLink`
Lineage is intra-Mohist-session: compact/reset rebinds **one** `AgentSession` to successive runtime sessions (`AgentSession.Transitions.cs:93-115`). **Decision:** record the chain on the server.
- **State:** add an ordered `RuntimeSessionLineage` list to `AgentSessionStatusSnapshot` (`AgentSession.cs:86-95`), entries `{ AgentRuntimeSessionId, BoundAt }`. It starts with one entry; `RebindRuntimeSession` appends a new entry on each rebind (this is also where the old id is *currently discarded* — it is now retained as the previous entry). Keep it simple: a flat ordered list; predecessor/successor are derived by position, so no separate back-pointers.
- **Event:** extend `AgentSessionRuntimeBound` (`AgentSessionEvent.cs:11`) to also carry `PreviousAgentRuntimeSessionId`, so the transition remains observable/event-sourced and realtime consumers can render the link immediately.
- **DTO:** add `RuntimeSessionLineage` (or a derived `{ previousRuntimeSessionId, nextRuntimeSessionId }` for the current runtime session) to `AgentSessionMetadataDto` (`AgentSessionReadModels.cs:53-65`). The grain already projects status into this DTO.
- **UI:** new `CompactionLineageLink` component in the `session-health` widget, rendered in the sticky recovery region. Predecessor link is the common case (the page always shows the latest runtime session); the successor scenario is covered for completeness when a non-latest runtime session is in view.

Placement follows the conventions: authoritative lineage state is computed and persisted in the Agent/Sessions domain on the Server; the Web only reads the DTO and renders the link — it never reconstructs the chain from events. The stable identity remains `sessionId`; the lineage is a chain of mutable runtime facets, consistent with `conventions.md:28-35`.

**Navigation target:** runtime sessions of one Mohist session share one URL (`/<project>/issues/<n>/workflow/sessions/<sessionName>`, keyed by `sessionName` — `App.tsx:62`). To give lineage links a *distinct, navigable* target without inventing a new route, the link carries a query param `?rt=<runtimeSessionId>` and the session page anchors the transcript to the compaction boundary where that runtime session's context begins/ends. This satisfies "activating a lineage link SHALL navigate to the linked runtime session" while staying in scope.
- *Alternatives considered:* (a) derive lineage client-side from the realtime `AgentSessionRuntimeBound` event — rejected; events are observation-only and a new viewer would see no history, violating the source-of-truth rule; (b) a new `/sessions/<runtimeId>` route per runtime session — rejected as out of scope (large UX change, see Non-Goals); (c) store only `PreviousAgentRuntimeSessionId` instead of a chain — rejected; it cannot answer "successor" and loses older generations. The ordered chain is the minimal model that answers both directions.

### D4. New `CompactionCompactSummary` atop the transcript; per-round entry retained
**Decision:** add a `CompactionCompactSummary` component (in `session-health`) that renders a one-line summary of all of the session's compaction events (count, times, strategies, aggregate token reduction) atop the transcript body, visible without expanding any round. It is fed by the same compaction-event list already available to the transcript model. The existing per-round `CompactionTimelineEntry` (`SessionTimeline.tsx:303-320`) stays for the detailed before/after token counts and summary, satisfying the "per-round detail remains available" scenario. The summary is hidden when there are zero compactions.
- *Alternatives considered:* lift the existing `CompactionTimelineEntry` list out of rounds instead of adding a summary — rejected; it would duplicate the detail rendering and remove the in-round context. A dedicated aggregate summary composes cleanly atop the transcript.

### D5. Retain a bounded usage history on `AgentSession`; enrich the activity DTO (no new endpoint); render a sparkline
**Decision:** the server retains a short context-usage history per session as a bounded list on `AgentSession.Status` (e.g. `ContextUsageHistory: List<{ At, Percent }>`), appended on `usage.updated`/`context_health_update` and **capped + time-thinned** (target ~24 samples, last-N with coarse time bucketing) so state and payloads stay small — honoring "data model as simple as possible, only necessary attributes". This history is exposed by **enriching the existing `ActivityCardDto.Usage`** (`AgentSessionReadModels.cs:192-210`) with the history list — this is exactly the permitted relaxation in the dashboard-pulse spec ("MAY be enriched to carry a short context-usage history … the only permitted relaxation … no independent new endpoint"). No new query route is added. The web maps it into the card model (`activity-cards.ts:26-55`), and a new `ContextUsageTrendMiniChart` (lightweight inline SVG sparkline, no charting dependency) renders it on `CompactSessionCard`. The chart degrades to hidden (`< 2` samples) per the graceful-degradation scenario.
- *Alternatives considered:* (a) accumulate history client-side from the realtime feed — rejected as primary because history is lost on reload and inconsistent across viewers, so "trend over the session lifetime" fails for a freshly-opened Pulse; (b) compute history server-side by scanning the event log on each activity query — rejected for query cost and because events may be pruned; (c) add a dedicated `/sessions/{id}/usage-history` endpoint — explicitly disallowed by the Pulse spec. Retaining a capped, thinned history on the grain and projecting it through the existing activity DTO is the minimal correct channel.

## Risks / Trade-offs

- **[Historical sessions have no lineage]** Pre-existing sessions compacted before this change have no `RuntimeSessionLineage`. -> The DTO fields are optional; the UI renders no link when the chain has a single entry. No backfill is required (the old id is genuinely lost for past compactions); the chain is populated going forward.
- **[Lineage navigation ambiguity]** Runtime sessions share one page URL, so a lineage link cannot open a truly distinct transcript view today. -> Mitigated by the `?rt=<runtimeSessionId>` anchor scheme (D3); a proper per-runtime-session view is an explicit Non-Goal / Open Question.
- **[Usage-history state growth]** Appending a sample on every usage update could bloat grain state and the activity payload. -> Mitigated by a hard cap + time-thinning (D5); the list is bounded regardless of session length.
- **[Sticky recovery bar consumes vertical space]** On short viewports a pinned bar reduces the visible transcript area. -> Mitigated by pinning only the recovery sub-region (not the whole header) and keeping it compact; the context-health bar already collapses cleanly.
- **[Alert-treatment perception]** A glyph + colored text may still be missed by some users. -> Mitigated by `aria-live`/`role="alert"` for red so assistive tech announces it; the session-page `ContextHealthBar` warning banner remains the strongest signal where space allows.
- **[DTO enrichment widens the activity payload]** Adding history to every activity card increases response size. -> Mitigated by the cap; history is a handful of small `{at,percent}` pairs per card.

## Migration Plan

No version-compatibility obligation (per `AGENTS.md`), so this is an additive, forward-only change.

1. **Server (Agent/Sessions domain):**
   - Extend `AgentSessionStatusSnapshot` with `RuntimeSessionLineage` and `ContextUsageHistory`; update the grain rehydration/persistence.
   - Update `RebindRuntimeSession` to append a lineage entry (retaining the predecessor) and extend `AgentSessionRuntimeBound` with `PreviousAgentRuntimeSessionId`.
   - Append a thinned usage sample where usage is recorded.
   - Project both into `AgentSessionMetadataDto` and `ActivityCardDto.Usage` via the existing querier mapping; keep fields nullable/optional.
2. **Web:**
   - Enhance `ContextHealthIndicator` alert treatment (D1); update/extend its tests for the new affordance and quiet-green behavior.
   - Make the recovery-bar region sticky (D2); add a scroll-stick test.
   - Add `CompactionLineageLink` (D3) and `CompactionCompactSummary` (D4) to the `session-health` widget and wire them into the session page.
   - Add `ContextUsageTrendMiniChart` (D5), map history in `activity-cards.ts`, render on `CompactSessionCard`.
3. **Tests (Fake-based, per project rules):** (a) indicator alert color/tooltip/aria at yellow/red and quiet/hidden at green/no-data across list/card/page; (b) recovery bar remains visible after transcript scroll; (c) lineage link navigates to the linked runtime session (`?rt=` anchor); (d) compaction events render in the compact summary without expanding a round, per-round entry still present; (e) trend mini-chart renders from history and degrades to hidden when history is empty/insufficient. No real external systems (use Fakes for the activity/session sources).
4. **Rollback:** all DTO fields are optional and all UI components degrade to hidden when data is absent, so reverting the server fields silently restores prior behavior on the web. `dotnet build` / web `typecheck` + `test:run` / runner `typecheck` + `test` gate the change.

## Open Questions

- **Lineage navigation depth:** Is the `?rt=<runtimeSessionId>` same-page anchor (D3) acceptable, or does the team want a first-class per-runtime-session transcript route? The latter is a larger UX change and is currently a Non-Goal; resolve before implementation if a distinct URL is required.
- **Usage-history sampling policy:** exact cap and thinning cadence (proposed: ~24 samples, ~30 s buckets). Confirm against real long-running sessions to balance "lifetime trend" against payload size.
- **Trend on the session page:** the specs scope the trend mini-chart to Pulse compact cards. Should the session-page recovery region also show the trend (it already has the snapshot bar)? Default: no, keep the page on the snapshot bar to avoid duplication unless desired.
