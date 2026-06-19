## Context

Issue Detail (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`) currently renders the Activity event timeline as an inline panel in the main content column (`EventTimelinePanel` at `IssueDetailPage.tsx:641`), between the commits area and Comments. The panel loads its full event history on page open via `useEventTimeline` → `useIssueEvents(issueNumber)` (`useEventTimeline.ts:98`, `queries.ts:59`), and subscribes to live events through `onTimelineEvent` (`useEventTimeline.ts:108`). Separately, `RuntimeDecisionSurface` (`RuntimeDecisionSurface.tsx:215`) renders a full-surface colored card driven by `toneClass` (`RuntimeDecisionSurface.tsx:79-87`, e.g. `bg-blue-50 border-blue-200`) per runtime state.

The proposal moves Activity into an on-demand dialog (lazy-loaded), reduces first-screen color noise, and tightens the page's spacing rhythm. This is a Web-frontend-only change: no API, CLI, or schema contract changes. The events endpoint, filter/sort/detail-expand behaviors, and live event vocabulary are all preserved. Existing dialog conventions exist (`WorkflowYamlDialog` at `IssueDetailPage.tsx:144` uses the Radix-based `Dialog` from `@/shared/ui/components/dialog` with a lazy `useWorkflowYaml(workflowRunId, open)` query at `queries.ts:96`), giving us a proven lazy-load + dialog pattern to mirror.

Stakeholders: anyone reading Issue Detail (the page is the primary per-issue work surface). Risk is medium because the change spans several components and alters the primary "how do I get to Activity" flow, but it is isolated to one route.

## Goals / Non-Goals

**Goals:**
- Remove the Activity timeline from the Issue Detail main content column; expose it via an `Activity` entry that opens a dialog.
- Lazy-load event history only when the dialog opens; never fetch events on initial page load.
- Guarantee events that arrive while the dialog is closed are not lost (full persisted history on reopen).
- Apply neutral-monochrome rendering to regular events; reserve color for failure/attention only.
- Change `RuntimeDecisionSurface` from a full-surface colored card to a neutral card with a colored edge accent, keeping all states visually distinguishable.
- Establish a consistent spacing rhythm on Issue Detail (group-tight + group-gap, whitespace over decorative borders).
- Keep the dialog fully functional on mobile (near-fullscreen) with no capability loss.

**Non-Goals (from issue):**
- Task dedup across `StepList` and `TaskProgressPanel`.
- State-based module clipping (hiding Profile/Actions for done issues, etc.).
- Runtime connection toast handling.
- Full responsive rebuild (StageBar collapse, right-rail waterfall). Only the components touched by this change (especially the Activity dialog) need mobile adaptation.
- `IssueWorkflowProfileEditor` relocation; header control de-duplication.
- A dedicated event-count endpoint.

## Decisions

### D1: Reuse `EventTimelinePanel` as the dialog body; gate its query with `enabled = isOpen`
`EventTimelinePanel` already owns filter/sort/detail-expand (`EventTimelinePanel.tsx:25`). We render it unchanged inside the dialog. `useEventTimeline`/`useIssueEvents` already accept an `enabled` flag (`queries.ts:59`); we thread the dialog `open` state through so the history query only runs when open — the exact pattern `WorkflowYamlDialog` uses (`IssueDetailPage.tsx:144`, `useWorkflowYaml(id, open)`).
- *Alternative considered:* Build a fresh dialog-internal timeline component. Rejected — duplicates working filter/sort/expand logic and risks divergence.
- *Alternative considered:* Keep the panel mounted but hidden. Rejected — defeats the lazy-load/no-initial-fetch goal.

### D2: Reuse the Radix `Dialog` with responsive classes for the near-fullscreen mobile sheet; introduce no new primitive
Desktop: a wide scrollable dialog (e.g. `sm:max-w-2xl max-h-[85vh]` with an internal scroll body), matching `WorkflowYamlDialog`'s `max-h-[80vh] overflow-hidden flex flex-col` shape. Mobile: near-fullscreen via responsive classes on `DialogContent` (e.g. `w-full h-[100dvh] sm:h-auto sm:rounded-lg inset-0 sm:inset-auto`) rather than a centered small box.
- *Alternative considered:* Add a dedicated `Sheet` primitive (Radix Popover/Drawer). Rejected for this scope — responsive `Dialog` classes satisfy "near-fullscreen sheet" with no new shared component and inherited focus-trap/ESC semantics.
- *Trade-off:* A true bottom-sheet feel is weaker than a purpose-built Sheet; acceptable since the issue only requires "not a small centered box".

### D3: Scope live accumulation to the open dialog; recover closed-period events by refetch on reopen
Because `EventTimelinePanel` (and thus the `onTimelineEvent` subscription in `useEventTimeline.ts:108`) only mounts while the dialog is open, live accumulation naturally stops while closed. The global live path in `IssueDetailPage` (cache invalidation + toasts) stays untouched, so the issue still refreshes while closed. On reopen, the events query refetches the full persisted history. To guarantee freshness (no stale snapshot), invalidate `['issue-events', number, projectId]` (or set the query `staleTime: 0`) when the dialog opens.
- *Alternative considered:* Keep the live subscription always-on and buffer events while closed. Rejected — wastes resources and re-implements dedupe the persisted endpoint already provides.
- *Alternative considered:* Rely solely on React Query default refetch-on-mount. Rejected as flaky (cached-fresh windows could show stale data right after activity); explicit invalidation on open is deterministic.

### D4: Color policy lives in `EventTimelineRow` — neutral default, attention accent only
Current rendering (`EventTimelineRow.tsx`): a per-category colored badge + dot from `CATEGORY_STYLES` (`:76-79`, `:66-69`) and a full-row attention tint `attentionBg` (`:38-42`). New policy:
- Regular categories (workflow/approval/integration/success/metadata): neutral monochrome dot, **no category badge**, no colored background.
- Failure/attention: keep a colored dot + halo ring (the `ring-2` accent), but **drop the full-row tinted background** (`attentionBg` → transparent).
- Detail block (`:103` `bg-gray-900`) → neutral light background (e.g. `bg-muted`/`bg-gray-50`).
- Live entrance animation (`:52` `animate-in slide-in-from-top-2`) → removed/converged.
Category *filters* (still labeled) remain, so users keep the ability to narrow by category even though categories are no longer color-coded.
- *Alternative considered:* Keep category colors but desaturated. Rejected — the issue explicitly wants neutral-as-default with color reserved for attention.

### D5: `RuntimeDecisionSurface` → neutral card + colored left edge accent
Replace `toneClass` (`bg-<tone>-50 border-<tone>-200`) with a neutral surface (`bg-white border-gray-200`) plus a colored left accent (`border-l-4 border-l-<tone>-400`). Keep the colored icon and the uppercase label chip so each state remains scannable; move headline/body text to neutral ink (`text-gray-900`/`text-gray-700`) to drop the saturated title/body tones. State distinguishability is carried by (left accent + icon + label) rather than by a full-surface fill, satisfying the spec invariant.
- *Alternative considered:* Keep the tinted background but lighter. Rejected — still stacks colored blocks and competes for the visual center.
- *Trade-off:* Slightly less vivid state signal; mitigated by retaining the colored icon + label + left border and covering with the existing `RuntimeDecisionSurface` tests.

### D6: Density via a documented spacing rhythm, not a new token system
Audit Issue Detail's ad-hoc gaps (`gap-6`, `space-y-6`, `mb-6`, card `p-4` + borders) and apply one rhythm: tight intra-group spacing (`space-y-1`/`gap-2`), larger inter-group spacing (e.g. `space-y-8`/`gap-8`) between major sections and right-rail cards, and whitespace grouping in place of decorative card borders where a border carries no information. First-screen next-action area (`RuntimeDecisionSurface`) gets explicit breathing room (margin/padding) rather than sitting flush against neighbors.
- *Alternative considered:* Introduce a formal design-token/spacing-scale system. Rejected — out of scope for one route; a disciplined Tailwind rhythm achieves the goal without a new abstraction.

### D7: `Activity` entry in the header, no count badge
Place the `Activity` button in the title/header area alongside the existing `Edit issue` control (`IssueDetailPage.tsx:~453`), exposing `aria-label="Activity"`. No count is shown before first open (avoids any pre-open events fetch). The component surfaces an icon + label only.
- *Alternative considered:* Show a live unread/failure count. Rejected — counted as a separate concern by the issue and would force eager event loading or a new endpoint.

## Risks / Trade-offs

- **[Events arriving while the dialog is closed look "lost"]** → Mitigation: on every open, invalidate + refetch `['issue-events', …]`; the dialog always reflects full persisted history. Global cache invalidation + toasts continue independently so the page itself stays live.
- **[Stale cached events shown immediately on reopen]** → Mitigation: explicit `invalidateQueries` on open (or `staleTime: 0` for the events query) so the freshest history wins; loading skeleton already exists (`EventTimelinePanel.tsx:151`).
- **[Neutralizing color reduces at-a-glance state/failure scanning]** → Mitigation: failure/attention keep a colored marker accent; `RuntimeDecisionSurface` keeps colored icon + label + left border. Covered by `EventTimelinePanel.test.tsx` and `RuntimeDecisionSurface.test.tsx` assertions on distinguishability.
- **[Dialog accessibility/mobile regression]** → Mitigation: reuse Radix `Dialog` (focus trap, ESC, overlay) and add responsive near-fullscreen classes + mobile hit-target test.
- **[No shareable URL for the event stream]** → Accepted trade-off (called out in the issue); staying in-context and matching existing dialog conventions outweighs deep-linkability.
- **[Density edits regress existing Issue Detail layout tests]** → Mitigation: keep `data-testid`s stable, update affected assertions, add spacing-rhythm/whitespace-grouping regression coverage.

## Migration Plan

- **Scope:** Frontend-only (`packages/web`). No API/CLI/schema migration; the events endpoint, event vocabulary, and filter/sort/expand contracts are unchanged.
- **Rollout:** Single change set behind the normal build; no feature flag required (isolated to one route, reversible by revert).
- **Test updates:** `EventTimelinePanel.test.tsx` (neutral rendering, no badge, no full-row tint, neutral detail block, no enter animation), `RuntimeDecisionSurface.test.tsx` (neutral bg + left accent, per-state distinguishability), `IssueDetailPage.test.tsx` (no inline Activity panel, `Activity` entry opens dialog, no events fetch on initial load, events fetch on open, mobile near-fullscreen).
- **Rollback:** Revert the commits; no data or persisted-state migration is involved.

## Open Questions

- Desktop dialog width and scroll strategy — `sm:max-w-2xl` vs `sm:max-w-3xl`, and whether the filter bar stays sticky inside the scroll body.
- Whether dialog open-state should be reflected in the URL (e.g. `?activity=1`) for shareability — currently leaning no, per the issue's accepted trade-off.
- Whether to retain the pulsing `Live` badge inside the dialog header or drop it given the converged animation policy.
- Degree of text neutralization in `RuntimeDecisionSurface`: keep the colored icon + label chip (proposed) vs also neutralizing the label chip to tone-only-on-hover.
