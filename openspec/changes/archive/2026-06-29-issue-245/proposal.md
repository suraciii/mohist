## Why

Context exhaustion currently fails silently: the list/card indicators show only a subtle percentage dot, the session-page recovery bar scrolls out of view, compaction events are buried inside expanded transcript rounds, and there is no navigable link between a session's pre- and post-compaction runtime sessions. Users are forced to open each session page to discover that context is about to run out, by which point the run has already stalled. This change makes session context health a first-class, proactively visible signal so users can act before a silent failure.

## What Changes

- **Proactive context-health alerting**: the shared `ContextHealthIndicator` SHALL render an explicit alert treatment (alert color + descriptive tooltip) when usage crosses the yellow/red thresholds, in every surface it appears in — session list rows (`SessionCard`), Pulse compact cards, and the session page — rather than a quiet percentage dot the user must interpret.
- **Sticky recovery bar**: the recovery bar on `SessionPage` SHALL stay visible while the transcript scrolls, so the Compact/Reset actions and context-health bar remain reachable at all times.
- **Compaction lineage linking**: the system SHALL expose an explicit, navigable relationship between a session and the runtime session it was compacted/reset from and the one it produced (the `NewAgentSessionId` rebind), surfaced as a link in the UI instead of an invisible implementation detail.
- **Compaction timeline compact view**: compaction events SHALL be visible in a compact summary without expanding individual transcript rounds; the per-round `CompactionTimelineEntry` remains for detail.
- **Context usage trend mini-chart**: compact cards SHALL render a small context-usage trend over the session lifetime, not just the latest snapshot.

## Capabilities

### New Capabilities
- `session-health`: Context-health presentation contract — threshold-based alert color/tooltip behavior of the shared `ContextHealthIndicator`, the compaction lineage relationship between pre- and post-compaction runtime sessions (and its navigable UI link), the compaction timeline compact summary, and the context-usage trend visualization.

### Modified Capabilities
- `agent-session-ui`: The recovery bar requirement is extended — the recovery bar SHALL remain visible (sticky) while the transcript body scrolls, in addition to rendering as a sub-region of the session header. Compaction events SHALL also be surfaced in a compact summary atop the transcript rather than only inside expanded rounds.
- `dashboard-pulse`: The compact candidate card requirement is extended — the card SHALL render the context-health alert treatment and a context-usage trend mini-chart. This requires the live activity source to carry a short usage history (the current source exposes only the latest snapshot), which relaxes the "derive exclusively from existing live sources / no new endpoint" constraint to the extent needed to supply that history.

## Impact

- **Web / session-health widget** (`packages/web/src/widgets/session-health/`): `ContextHealthIndicator` gains the alert treatment; new compaction-lineage link component; new compaction compact-summary component; new context-usage trend mini-chart component; extend `model/context-health.ts` as needed.
- **Web / session page** (`packages/web/src/pages/session/ui/SessionPage.tsx:565`, `SessionHeader`): make the recovery-bar region sticky within the page scroll context; wire the compaction lineage link and compact compaction summary.
- **Web / coder-session** (`packages/web/src/widgets/coder-session/ui/SessionTimeline.tsx:303`, `SessionCard.tsx:165`): surface the compaction compact summary atop the timeline and apply the indicator alert treatment in list rows.
- **Web / dashboard-pulse** (`packages/web/src/widgets/dashboard-pulse/ui/CompactSessionCard.tsx:90`): render trend mini-chart + alert treatment.
- **Server / API & data** (`packages/server/src/Mohist.Server/Sessions/`): compaction lineage requires surfacing the prior/next runtime-session relationship on the session metadata or compaction event (currently the `NewAgentSessionId` rebind in `AgentSessionGrain.cs:118` is recorded but not exposed as a navigable link). The context-usage trend requires the activity/session read-model to retain a short usage history rather than only the latest snapshot; the live activity feed (`activity-cards.ts:50`, `SessionCard`) must carry that history to the Pulse cards without violating the read-only Pulse contract beyond the noted relaxation.
- **Tests** (Fake-based, per project rules): (a) indicator shows alert color+tooltip at yellow/red thresholds and stays quiet when green/no-data, across list/card/page surfaces; (b) recovery bar remains visible after scrolling the transcript; (c) compaction lineage link navigates between the linked runtime sessions; (d) compaction events render in the compact summary without expanding a round; (e) trend mini-chart renders from usage history and degrades gracefully when history is empty.
- **Non-goals** (explicitly out of scope): no server-side auto-compaction trigger, no cross-session global health monitoring, no "rounds remaining" estimation, no changing Compact/Reset availability on active sessions.
