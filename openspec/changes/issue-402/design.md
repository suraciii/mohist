## Context

Activity and Coder Session are the diagnostic views an owner relies on during failures and quality-gate decisions. Today neither serves as a useful execution evidence view:

- **Activity** (`pages/activity/ui/ActivityPage.tsx`, 131 lines) is a session-only feed: three sections (Active / Waiting / Recent) of `SessionCard`s, a runner badge, and a usage snapshot. Entries carry no event-type identity — every entry is "a session" — and there is no way to surface failures/approvals above routine noise. Cards link only to the issue (`/issues/:number`), never directly to the session. The server-side activity feed already includes generic `agent-launch` sessions, but the Web `AgentActivitySession` type omits their `agentId` / `agentName` fields and the card projection treats every row as issue-bound, so generic sessions are not presented correctly. A full event-timeline widget (`widgets/issue-event-timeline/`) already exists with categories, attention markers, filtering, and live badges, but it is surfaced only as a per-issue modal (`ActivityDialog`); it is not reused on the Activity page.
- **Coder Session** (`pages/session/ui/SessionDetailShell.tsx`, 702 lines) already has some region separation (header → usage strip → scroll container with sticky title + sticky recovery bar → followup composer → sibling right rail), but task identity, status, failure reason, failure category, tool-error counts, and usage are all packed into the 220-line `SessionHeader`. Errors are buried in the header/transcript rather than having their own evidence region. The layout degrades unevenly for generic agent sessions (no siblings, no lineage, recovery actions gated off).
- **Status presentation** uses hardcoded Tailwind palettes everywhere: `StatusBadge` (`bg-blue-100 text-blue-700`, `bg-red-100 text-red-700`, …), `STAGE_COLORS` in `SessionCard`, `CATEGORY_STYLES` in `issue-event-timeline` (`bg-red-500`, `bg-red-50`, `text-red-700`), `ContextHealthBar`/`ContextHealthIndicator`, `SessionRecoveryActions`, `SessionFollowupComposer`, `StatusBar`, sibling sidebar status dots. The shared `success`/`warning`/`info`/`danger` (+ `-subtle`/`-border`/`-foreground`) CSS token families from issue 398 are defined in `app/styles/index.css` (light + dark) and are usable as Tailwind utilities, but the central `shared/status-presentation/` helper layer added in 398 was later rolled back. The surviving pattern is inline `Record<State, 'bg-*-subtle text-* border-*-border'>` maps (e.g. `pages/issue-detail/ui/pills.tsx:18`, `StatusHeadline.tsx:34`, `shared/ui/components/card-section.tsx:21`).

Constraints: no event-recording or event-subscription changes — the Activity evidence view consumes existing recorded events via a new project-scoped read endpoint (`GET /api/projects/:projectRef/events`) that queries already-stored events without changing how they are recorded. No new event-stream or subscription endpoints. No runner / CLI changes. No new external dependencies. Tests must follow `design/testing.md` (MSW, no `vi.mock`, fake time, DI seams, `data-testid` + `data-*` state assertions).

Stakeholders: issue owners reading evidence during failures and quality-gate decisions; the milestone epic #45 aligning Web UI with the production-console positioning.

## Goals / Non-Goals

**Goals:**
- Restructure Activity into an event-level evidence view where each entry carries an event-type identity (issue state change / workflow stage change / agent session event / runner event / failure) and an attention level (failure / approval / blocked / routine).
- Surface needs-attention and failure events above routine low-priority events; collapse the attention zone gracefully when empty.
- Let the owner group/filter entries by event type or attention level without opening a per-issue dialog.
- Surface generic agent-launched sessions alongside workflow-bound sessions on Activity.
- Provide orientation-preserving navigation entry points from Activity (issue / session / workflow context / runner detail) and from Coder Session (issue / workflow context / sibling / lineage / agent context).
- Reorganize Coder Session into a stable, scannable layout with defined regions for task identity, current status, turns/tool calls, errors, token/context usage, and siblings — each with a recognizable place, degrading gracefully when a region has no data.
- Migrate all status/surface presentation in both views to the shared theme-token families (light + dark), replacing every hardcoded `bg-red-*`/`text-red-*`/`bg-yellow-*`/`bg-amber-*`/`bg-blue-100`/… palette class.

**Non-Goals:**
- Do not redesign Files or Diff.
- Do not change how events or session transcripts are recorded, emitted, or subscribed.
- Do not add new event-stream or event-subscription endpoints; a project-scoped event read endpoint that queries existing recorded events is permitted and does not violate the issue non-goals about event recording.
- Do not reintroduce a shared `shared/status-presentation/` helper module (that was rolled back after issue 398; re-litigating it is out of scope).
- Do not expose raw internal implementation field names as product-facing labels.
- Do not change recovery/followup/cancel action gating semantics or the data they consume.

## Decisions

### D1 — Activity requires an event-level feed; current snapshots are supplementary, not transition evidence

The Activity page will render a unified `ActivityEvent[]` where each entry carries `{ type, attention, time, title, description, targets }`. `type ∈ { issue-state, workflow-stage, agent-session, runner, failure }`; `attention ∈ { failure, approval, blocked, routine }`. Existing snapshots can supplement that feed, but they cannot provide all required transition evidence:

- `useAgentActivity()` (5s refetch) → current sessions + approval waiting cards + summary. The feed already contains workflow-bound and generic `agent-launch` sessions. These rows can seed current `agent-session` evidence and approval attention, but their production status is only `active` / `inactive`; `completedAt` and `failureReason` are currently null, so they cannot be treated as reliable start/end/failure transitions.
- `useRunners()` (5s refetch) → current runner rows. Busy runners can contribute routine runner state and stale/offline runners can contribute blocked attention targeting `/runners/:runnerId`; an idle runner remains summarized by `RunnerSummaryBadge`.
- Recorded issue/workflow/session/runner transitions → required for actual `issue-state`, `workflow-stage`, lifecycle, and failure entries. A new project-scoped event read endpoint (`GET /api/projects/:projectRef/events`) queries already-recorded events across issues, workflow runs, and agent sessions within a project, sorted by time. This reads existing stored events without changing how they are recorded and without adding subscription behavior. T-000 delivers this endpoint.

The project-scoped recorded-event input is delivered by T-000 as a new read-only endpoint. It queries existing recorded events without changing event recording or adding subscription behavior, so it respects the issue's non-goals. T-002 depends on T-000 and must not infer transitions from current-state snapshots.

**Alternatives considered:**
- *Reuse `EventTimelinePanel` directly on Activity.* Rejected: the panel consumes `useIssueEvents(issueNumber)` + a per-issue live `onTimelineEvent` subscription — it is scoped to one issue. Activity is cross-issue. Even with the new project-scoped event read endpoint, reusing the panel directly would require extending its live subscription model cross-issue, violating the "no new event subscription" non-goal. The projection in D3 normalizes the project-scoped recorded events into the `ActivityEvent[]` shape instead.
- *Keep session cards and just add event-type badges.* Rejected: the spec explicitly says the page "SHALL NOT be limited to active/waiting/recent session cards without an event-type identity on each entry" and wants failures/approvals surfaced above routine events — a re-badge of the existing three-section grid cannot express attention ordering across sections.

### D2 — Two-zone attention layout with collapsible attention zone

The Activity feed is split into two zones in reading order:

1. **Attention zone** (`data-testid="activity-attention-zone"`) — rendered **only when it has entries**. Holds `failure`, `approval`, and `blocked` events, danger/warning-toned. When empty, the zone is absent (no empty placeholder competing with routine activity — per spec scenario "No needs-attention events yields a normal routine view").
2. **Routine zone** (`data-testid="activity-routine-zone"`) — the remaining events in attention-ordered reading order (most recent first within each type).

A filter bar (`data-testid="activity-filter-bar"`, reusing the `CategoryFilter` chip pattern from `widgets/issue-event-timeline/ui/CategoryFilter.tsx`) offers chips per event type plus an "Attention only" chip. Filtering operates on the `type`/`attention` identity entries already carry. Clearing filters restores the full two-zone view.

**Alternatives considered:**
- *Single flat list sorted by an attention weight score.* Rejected: the spec accepts a "dedicated attention zone" and explicitly forbids reserving an empty one — a zone that collapses when empty matches that wording better than a weight-sorted list where attention entries are interleaved with routine ones.
- *Keep the Active/Waiting/Recent sections and add a fourth "Attention" section.* Rejected: that preserves the session-card framing the spec wants to move away from, and "Waiting" is already the approval set — folding it into the attention zone is cleaner.

### D3 — Event identity is normalized in an `activity-events` projection module consuming the project-scoped event endpoint

A new pure module `widgets/coder-session/model/activity-events.ts` exports `buildActivityEvents(input): ActivityEvent[]` and a `useActivityEvents()` hook composing the project-scoped event endpoint (T-000) with `useAgentActivity` + `useRunners`. `useGlobalAgentSessions` is not composed because it is another project-wide view of the same sessions and would duplicate rows already returned by `useAgentActivity`. Derivation rules are pure functions over those inputs:

- Recorded issue/workflow/session events → their corresponding production event type and attention level; a recorded failed lifecycle event also retains the separately recorded session lifecycle evidence.
- Activity session snapshot → current `agent-session` evidence only; `inactive` SHALL NOT be relabeled as completed, cancelled, or failed without a terminal fact.
- Waiting entry (`label === 'Needs Approval'`) → `approval` attention. The current waiting DTO does not encode a blocked variant.
- Runner busy → `runner` event with routine attention; runner stale/offline → `runner` event with blocked attention.
- Generic `agent-launch` row → `agent-session` evidence using `agentId` / `agentName` and the generic-session route, without fabricating issue context.

Labels are expressed in production/domain terms ("Issue #42 moved to review", "Session failed: context limit", "Runner stale"). Raw field names are not surfaced. This is a unit-testable pure module (`.dom.test.ts` for the builder, collocated).

**Alternatives considered:**
- *Derive events inline in the page component.* Rejected: untestable in isolation, and the page is already a 131-line component with DI seams — the projection belongs in the widget model layer next to `activity-cards.ts`.

### D4 — Coder Session formalizes six evidence regions plus a sticky orientation affordance; the errors region is new

The shell keeps its current structural bones (they already satisfy "regions, not a single scroll") but formalizes them into six named, testable regions. The only structural addition is the **errors evidence region**; everything else is repositioning + token migration.

| Region | `data-testid` | Placement | Degrades when |
|---|---|---|---|
| Task identity + status | `session-header` (new) | Top of main column, non-sticky | always present |
| Sticky identity/status | `session-sticky-title` (existing) | Sticky `top-0` inside scroll container | always present |
| Usage summary | `session-usage-summary` (existing) | Strip below header | omitted if no usage fields |
| **Errors evidence** | `session-errors-region` (new) | Compact strip below usage, above transcript | omitted if no failure reason / category / tool errors |
| Transcript (turns + tool calls) | `session-transcript-scroll-container` (existing) | Scrollable main area | empty/waiting states as today |
| Followup composer | `session-followup-composer` (existing) | Bottom of main column | disabled state as today |
| Sibling sidebar | `session-sibling-sidebar` (existing) | Right rail on `xl:` | omitted when no siblings (generic sessions) |

The **errors region** derives session-failure evidence from `statusKind === 'failed'`, `meta.eventSummary.failureCategory`, and the tool-error count (already computed for the header), and renders `meta.failureReason` only when a source provides one. This avoids depending on `failureReason`, which both current issue-bound and generic metadata builders set to null, while still surfacing a failed session. The compact danger-toned summary lets the owner discover failure without scrolling the transcript. It is read-only; the existing sticky **recovery bar** (`session-recovery-bar`, with `ContextHealthBar` + `SessionRecoveryActions` + `CompactionLineageLink`) stays inside the transcript scroll container exactly where it is — moving it would break `SessionPage.sticky.test.tsx` and change recovery gating.

**Alternatives considered:**
- *Make the errors region sticky alongside the recovery bar.* Rejected: the spec says summary regions "SHALL be compact" and the transcript "SHALL NOT push the summaries off-screen" — a non-sticky strip above the scroll container already satisfies that without competing with the sticky title/recovery bar stack.
- *Fold errors into the usage summary strip.* Rejected: errors are a distinct evidence class and the spec lists them separately; co-locating them with usage would bury failures again.

### D5 — Theme-token migration via inline `Record<State, tokens>` maps, no shared helper module

Each view owns a small inline `statusPresentation: Record<StatusKind, { surface; text; border; dot? }>` record mapping a status/event kind to the shared token utilities (e.g. `failed → 'bg-danger-subtle text-danger border-danger-border'`). This mirrors the proven pattern in `pages/issue-detail/ui/pills.tsx:18` (`runtimeSummaryPresentation`) and `StatusHeadline.tsx:34`. Specifically:

- `SessionDetailShell.tsx` `StatusBadge` config (lines 382–390) → `sessionStatusPresentation` record over tokens.
- `SessionCard.tsx` `STAGE_COLORS` (lines 31–37) + approval/failure pills → `stagePresentation` + `attentionPresentation` records over tokens.
- `issue-event-timeline/model/types.ts` `CATEGORY_STYLES` (lines 29–77) → token-based category styles; `EventTimelineRow.tsx` marker accents (`bg-red-500`/`bg-amber-500`) → `danger`/`warning` tokens.
- `ContextHealthBar.tsx` / `ContextHealthIndicator.tsx` `DOT_CLASS`/`TEXT_CLASS`/`CONTAINER_CLASS` → token records.
- `SessionRecoveryActions.tsx` error surface (`border-red-300 bg-red-50 text-red-800`), `SessionFollowupComposer.tsx` sent/error text, sibling sidebar status dots, `StatusBar.tsx` counts → tokens.
- Semantic tool success/failure treatment in `widgets/session-transcript/ui/tool-views/index.tsx` and `bash-view.tsx` → success/danger tokens. Diff syntax/addition/deletion colors are content presentation, not status surfaces, and remain out of scope with the Files/Diff non-goal.

A shared `shared/status-presentation/` module is **not** reintroduced. Issue 398 added and then rolled back such a module; re-litigating that boundary is out of scope, and the inline-record pattern is already the established convention in the codebase.

**Alternatives considered:**
- *Reintroduce `shared/status-presentation/` with `StatusPill` + `statusTone`.* Rejected: out of scope, risks re-rolling back, and 398's rollback shows the boundary was contentious. The inline-record pattern is consistent with `pills.tsx`/`StatusHeadline.tsx`/`card-section.tsx`.
- *Leave the event-timeline palette hardcoded (it's only a modal).* Rejected: the spec scenario "Failure events use the shared danger tokens in both themes" applies to Activity, and the timeline categories are the natural source of event-type styling for the Activity feed (D3 reuses the category vocabulary).

### D6 — Navigation entry points are project-scoped `targets` on each entry / region

- **Activity entries** carry a `targets` object; the entry's primary link is the most specific available (session > issue > runner), with secondary targets as inline chips. Issue-bound sessions use the existing ID route `/issues/:number/session/:sessionId`; T-001 resolves that ID through `useCoderSessions` to the canonical session name before metadata/transcript/recovery queries. Generic sessions use `/agent-sessions/:sessionId`. Runner events target `/runners/:runnerId`. Workflow-stage events target the issue detail (`/issues/:number`) — there is no `/issues/:number/workflow` route today, and adding one is a non-goal here; the issue detail hosts the workflow sessions panel, so it is the workflow-context home. This is recorded as an open question.
- **Coder Session** keeps its existing back link (`backPath`/`backLabel` from the data source) and adds an explicit workflow-context entry point in the header for issue-bound sessions, linking to the issue detail (same reasoning). Direct links originating in Activity append `from=activity`; both issue-bound and generic data sources recognize that bounded value and use the project-scoped Activity page as the back target, while direct entries continue to fall back to issue or agent context. Generic sessions keep the conditional back link to agent context (`/agents/:agentId`) when no `contextRefs.issueNumber` exists — already implemented in `useGenericSessionDataSource.ts:126-134` and asserted by `GenericSessionPage.test.tsx:170-190`. Sibling and lineage links are unchanged.

All links go through `useProjectPath()` so they stay project-scoped; no root routes are introduced.

**Alternatives considered:**
- *Add a `/issues/:number/workflow` route as the workflow-context target.* Rejected for this issue: it pulls in routing + a new page shell and is beyond "restructure the two diagnostic views". Tracked as an open question.
- *Make Activity entries link only to issues (status quo).* Rejected: the spec scenario "Entry links directly to the executing session" requires a session link without first opening the issue.

### D7 — Tests: update existing anchors, add spec tests for the evidence contract

- **Activity** (`pages/activity/ui/ActivityPage.test.tsx`): update to assert event-type identity (`data-event-type` on each entry), attention zone presence/absence, filter behavior, and session/runner nav targets. Preserve `activity-runners-link`. New `data-testid`s: `activity-attention-zone`, `activity-routine-zone`, `activity-filter-bar`, `activity-filter-${type}`, `activity-event-entry` (with `data-event-type`, `data-attention`).
- **Coder Session** (`SessionPage.sticky.test.tsx`, `SessionPage.cancel.test.tsx`, `SessionPage.lineage.test.tsx`, `GenericSessionPage.test.tsx`): preserve every existing anchor listed in the spec ("transcript scroll container, sticky title, recovery bar, sibling navigation slot, cancel triggers"). Add assertions for `session-errors-region` presence/absence and content. New `data-testid`s: `session-errors-region` (with `data-failure-category`, `data-tool-error-count`).
- **New spec tests** under `packages/web/tests/` (`.spec.tsx`): cover the cross-region evidence contract — event-type identification, attention prominence on first screen, stable region places, navigation orientation (back path returns to originating context), and light/dark status-token consistency. These use MSW for the existing endpoints and the DI `dependencies`/`shellComponents` seams; no `vi.mock`. Time is controlled via `vi.useFakeTimers` + a `now` prop (the Activity `setInterval(1000)` and the anomaly 30s timer must be faked).
- **Unit tests** (collocated `.dom.test.ts`): `activity-events.ts` builder — recorded-event classification, snapshot classification without fabricated transitions, attention ordering, generic-session identity, deduplication, and target construction.

Existing `data-testid` anchors that remain valid are preserved; new anchors do not collide.

## Risks / Trade-offs

- **[Project-scoped recorded-event input]** → A new project-scoped event read endpoint (T-000) queries existing recorded events across issues, workflow runs, and agent sessions. The endpoint reads already-stored events without changing event recording or adding subscription behavior. `useAgentActivity` and `useRunners` snapshots remain supplementary; snapshots SHALL NOT be presented as transitions.
- **[Activity session terminal facts are absent]** → The activity assembler currently emits only `active` / `inactive`, sets `completedAt` and `failureReason` to null, and hardcodes completed/failed summary counts to zero. The project-scoped event endpoint (T-000) provides recorded lifecycle and failure events that the projection (T-002) uses for terminal-fact evidence; snapshots SHALL NOT be relabeled as terminal transitions.
- **[Filter chip duplication with `CategoryFilter`]** → The Activity filter bar reuses the `CategoryFilter` chip pattern but with a different category vocabulary (event types, not timeline categories). Mitigation: keep the two filters independent; if a shared chip component is wanted later, extract it in a follow-up, not here.
- **[No workflow-context route]** → Workflow-stage events and the session workflow-context entry point link to the issue detail, not a dedicated workflow page. Mitigation: the issue detail hosts `WorkflowSessionsPanel`, so it is the workflow-context home today; recorded as an open question for a future route.
- **[Token migration touches many files]** → Status presentation is spread across `SessionDetailShell`, `SessionCard`, `issue-event-timeline`, `ContextHealthBar`/`Indicator`, `SessionRecoveryActions`, `SessionFollowupComposer`, `StatusBar`, sibling sidebar. Mitigation: mirror the proven `pills.tsx` inline-record pattern; assert both light and dark mode in the new spec tests; the migration is mechanical and low-risk per file but broad, so it lands first in the migration order.
- **[Errors region must not change recovery gating]** → Extracting errors into a summary region could be mistaken for moving the recovery actions. Mitigation: the errors region is read-only; the sticky recovery bar with `SessionRecoveryActions` stays inside the transcript scroll container; `SessionPage.sticky.test.tsx` already pins its placement and stays green.
- **[Activity `setInterval(1000)` and anomaly 30s timer under fake-time rule]** → These are real timers in production code; tests must control them. Mitigation: use `vi.useFakeTimers` and a `now` prop where the hooks accept one; the DI seam already allows injecting a fake `activityCardsHook`.

## Migration Plan

Frontend change plus one new server read endpoint; no event-recording or event-subscription changes. Deploy = merge to `master`; rollback = revert the merge commit.

Implementation order (each step lands green before the next):

1. **Project-scoped event endpoint (T-000).** Add `GET /api/projects/:projectRef/events` querying existing recorded events across issues, workflow runs, and agent sessions within a project. Run server tests.
2. **Token migration (mechanical, lowest risk).** Introduce inline `statusPresentation` records and replace every hardcoded palette class listed in D5. Update existing token-usage guard tests if any. Run `npm run typecheck -w packages/web` + `npm run test:run -w packages/web`.
3. **Coder Session errors region + region formalization.** Add `session-errors-region`, reposition nothing else (regions already in place), wire `meta.failureReason`/`failureCategory`/tool-error count. Update `SessionPage.*.test.tsx` for the new region; keep all existing anchors green.
4. **Add the Activity projection.** Add `widgets/coder-session/model/activity-events.ts` + unit tests (`.dom.test.ts`) consuming the project-scoped event endpoint delivered in step 1. Current snapshots remain supplementary and may not fabricate transitions. No UI change yet.
5. **Activity two-zone feed + filter bar.** Replace the three-section session-card grid with the attention/routine zones and filter bar rendering `ActivityEvent[]`. Update `ActivityPage.test.tsx`; preserve `activity-runners-link`.
6. **Generic session identity.** Preserve generic rows already present in `useAgentActivity`, type their `agentId` / `agentName` fields, and assert generic + workflow-bound sessions appear exactly once together.
7. **Navigation entry points.** Add `targets` to Activity entries; harden the existing issue-session ID route resolution; add `from=activity` return orientation and a workflow-context entry point to the session header for issue-bound sessions. Assert project-scoped `href`s and back targets.
8. **New spec tests** under `tests/` for the cross-region evidence contract (event-type identification, attention prominence, stable regions, nav orientation, light/dark tokens).

Rollback strategy: revert the merge commit; no data migration, no schema, no server state. The two views return to their pre-issue session-card / 702-line-shell form.

## Open Questions

- **Workflow-context route.** Should there be a `/issues/:number/workflow` route as the canonical workflow-context target, or is linking to the issue detail (which hosts `WorkflowSessionsPanel`) sufficient? This issue links to the issue detail; a dedicated route is a candidate for a separate issue.
- **Attention-zone pagination/scroll.** When the attention zone grows large (many failures), should it collapse to a fixed-height scroll, or stay fully expanded above the routine zone? This issue keeps it expanded; a collapse affordance is a candidate refinement.
- **Activity event retention.** The snapshot-derived feed ages entries out as the underlying projection drops them. Is the current `useAgentActivity` `limit` sufficient for evidence retention, or should the view request a larger limit when filtered to a type? Deferred — no endpoint change in this issue.
