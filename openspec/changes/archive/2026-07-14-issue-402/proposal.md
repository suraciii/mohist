## Why

Activity and Coder Session are the diagnostic views an owner relies on during failures and quality-gate decisions, but neither currently serves as a useful execution evidence view. The Activity page is a session-only feed (active / waiting / recent cards) that does not distinguish issue state changes, workflow stage transitions, agent session events, runner events, or failures — the board-level event stream described in `docs/web-ui.md` lives only as a per-issue dialog, and the page has no way to surface needs-attention or failure events above normal noise. The Coder Session page packs task identity, status, turns, tool calls, errors, token/context usage, and sibling sessions into a single 700-line scroll with no stable information hierarchy, making it hard to scan what happened and whether action is needed. With the shared status/theme baseline from issue 398 now landed, both views can be reworked into evidence views that answer "what happened, and does it need action?" without re-litigating status or color.

## What Changes

- Restructure the Activity page so entries clearly distinguish event types: issue state changes, workflow stage changes, agent session starts/ends, runner events, and failures — not just session cards.
- Make needs-attention and failure events easier to find in Activity than normal low-priority events: failures, approvals, and blocked states surface above routine activity, with visual priority matching the shared status language.
- Reorganize the Coder Session page into a stable, scannable layout where task identity, current status, turns, tool calls, errors, token/context usage, and sibling sessions each have a defined place and do not compete in a single undifferentiated scroll.
- Add navigation entry points from Activity and Coder Session to the relevant issue, workflow context, or evidence so the user can follow the trail without losing orientation (including direct links to sessions from Activity, not only to issues).
- Apply the shared status and surface language (light and dark mode) consistently across Activity and Coder Session, replacing ad-hoc hardcoded palette classes (e.g. `bg-red-500` in `issue-event-timeline` category styles) with the theme-token families landed in issue 398.
- Surface generic agent-launched sessions on the Activity page alongside workflow-bound sessions, so the full execution picture is visible in one place.

Non-goals (per issue): do not redesign Files or Diff; do not change how events or session transcripts are recorded; do not add new event subscription behavior; do not expose hidden internal implementation fields as product concepts.

## Capabilities

- `activity-evidence-view`: The Activity page as an event-level execution evidence view — how it distinguishes event types (issue state changes, workflow stage changes, agent session events, runner events, failures); how needs-attention and failure events are surfaced above normal low-priority events; how entries are grouped or filtered by production meaning; and the navigation entry points from Activity to issues, workflow context, sessions, and runner detail.
- `coder-session-evidence-view`: The Coder Session page as an agent-task evidence trail — its stable, scannable information hierarchy (task identity, current status, turns, tool calls, errors, token/context usage, sibling sessions); how density and grouping make execution evidence readable; and the navigation entry points from Coder Session to the relevant issue, workflow context, and sibling or lineage evidence.

## Impact

- **Web** (`packages/web/src`):
  - Activity page: `pages/activity/ui/ActivityPage.tsx` — restructured from session-only feed to event-level evidence view with event-type distinction and priority surfacing.
  - Activity cards and projections: `widgets/coder-session/model/activity-cards.ts`, `widgets/coder-session/ui/SessionCard.tsx` — card model and rendering updated to carry event-type identity and priority; generic agent-launched sessions surfaced.
  - Event timeline: `widgets/issue-event-timeline/model/types.ts` — `CATEGORY_STYLES` migrated from hardcoded Tailwind palettes to shared theme tokens; timeline model evaluated for reuse in the Activity page.
  - Coder Session shell: `pages/session/ui/SessionDetailShell.tsx` — reorganized into a stable layout with defined regions for task identity, status, transcript, usage, errors, and siblings.
  - Session usage and recovery: `pages/session/ui/SessionUsageSummary.tsx`, `widgets/coder-session/ui/SessionRecoveryActions.tsx`, `widgets/coder-session/ui/SessionFollowupComposer.tsx` — repositioned within the new layout hierarchy.
  - Transcript evidence: semantic success/failure accents in `widgets/session-transcript/ui/tool-views/` migrated to the shared status-token families without changing Diff content rendering.
  - Session data sources: `pages/session/data/useIssueSessionDataSource.tsx`, `pages/session/data/useGenericSessionDataSource.ts` — navigation context (issue linkage, workflow context) exposed for orientation-preserving entry points.
  - Sibling sessions: `SiblingSessionsSidebar` in `pages/session/data/useIssueSessionDataSource.tsx` — repositioned as a stable reference region beside `SessionDetailShell`.
  - Status presentation: all status surfaces in both views routed through the shared theme-token families (`success` / `warning` / `info` / `danger` + `-subtle` / `-border` / `-foreground`) from issue 398, replacing ad-hoc treatment duplicates.
- **Server** (`packages/server/src/Mohist.Server/`): a new project-scoped event read endpoint (`GET /api/projects/:projectRef/events`) queries existing recorded events across issues, workflow runs, and agent sessions within a project, sorted by time. This reads already-stored events without changing how they are recorded and without adding event subscription behavior. The issue non-goals "do not change how events or session transcripts are recorded" and "do not add new event subscription behavior" are respected.
- **Runner / CLI**: none.
- **Dependencies**: none added.
- **Tests** (`packages/web`): `pages/activity/ui/ActivityPage.test.tsx` updated for event-type distinction and priority surfacing; existing `pages/session/ui/SessionPage.*.test.tsx`, `GenericSessionPage.test.tsx`, and `tests/SessionPageHeader.spec.tsx` updated for the new layout hierarchy; new spec tests cover event-type identification, needs-attention/failure prominence, stable session layout regions, navigation orientation, and light/dark status consistency. Existing `data-testid` anchors preserved where still valid. **Server** (`packages/server/tests/`): spec tests for the project-scoped event endpoint verifying cross-aggregate event retrieval, time ordering, and no mutation of event recording.
- **Risk (medium)**: this changes diagnostic views that users rely on during failures and quality-gate decisions. Mitigated by consuming recorded events via a read-only project-scoped endpoint, reusing the shared status baseline from issue 398, and asserting every event-type and layout path against the new evidence-view contract.
