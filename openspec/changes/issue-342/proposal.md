## Why

The issue detail page serves two opposite high-frequency mobile needs — "glance at status" and "act in a hurry" — but on a phone both fail: every primary action is buried under a long scroll of description, diff, commits, and comments, and confirming a destructive stop obscures the very status the user needs to see. #340 already converged the single primary-action model and #341 already delivered the read-only sticky status headline and the reading-flow/reference-rail layering; this change completes the mobile adaptation by relocating the primary action into a thumb-reachable bottom bar and replacing the obscuring confirmation with a bottom drawer that keeps the status headline visible.

## What Changes

- Add a **bottom floating action bar** on narrow viewports that surfaces the single primary action from `decision.primary` (start / stop / approve / retry / resume / rerun). It sits in the thumb zone, above the global `MobileBottomNav`, never overlapping it, and only renders when a primary action exists (done / no-primary states show no bar).
- Reserve **bottom padding** on the scroll body so the last comment / content is never hidden behind the floating bar or the global bottom nav.
- Replace the destructive (stop / send-back) confirmation on narrow viewports with a **bottom-sliding confirmation drawer** instead of an inline confirmation block or centered/full-screen dialog. While the drawer is open, the top sticky status headline stays visible, so the user can see "where the agent is right now" at the exact moment they decide to stop.
- On narrow viewports, **strip the action surface out of the status-header tier**: the header tier becomes strictly read-only (headline + stage + current task + identity). The `RuntimeDecisionSurface` actions relocate to the bottom bar; desktop/tablet keep the action surface anchored in the header tier unchanged.
- **BREAKING** (narrow-only, internal layout contract): the #341 invariant "Decision and Action Surface Anchored to the Header Tier" gains a narrow-viewport exception — on narrow viewports the primary action is no longer rendered in the header tier.
- Pin the **reference-rail narrow-screen behavior**: desktop right column (metadata + configuration) collapses into stacked, expandable sections that follow the reading flow, with low-frequency items (drift, convergence) collapsed by default. (Mostly delivered by #341; this change locks the invariant and ensures the collapse ordering is correct.)
- Tablet (`lg`, 1024px+) and above fully restore the desktop layout — no mobile-only element (floating bar, drawer) renders.

Out of scope: redesigning the global `MobileBottomNav` (its tab crowding is a separate systemic issue), any server/runner/CLI model or projection change, global command palette / keyboard shortcuts, and re-converging the action surface (already done by #340).

## Capabilities

- `issue-detail-mobile-action-bar`: The narrow-viewport bottom floating bar that holds the single primary action. Owns: deriving from `decision.primary`, rendering only when a primary action exists, sitting in the thumb zone above the global bottom nav without overlap, and the page bottom-padding reservation that prevents content from being obscured. (NEW)
- `issue-detail-confirmation-drawer`: The narrow-viewport bottom-sliding drawer for destructive-action confirmation (stop, send-back). Owns: sliding in from the bottom edge, keeping the top sticky status headline visible during confirmation, and presenting the irreversibility/recoverability consequence and confirm/cancel controls. (NEW)
- `issue-detail-status-header`: MODIFIED — adds the narrow-viewport read-only variant. On narrow viewports the header tier strips the action surface (it becomes headline + stage + current task + identity only); on desktop/tablet the existing #341 invariant (action surface anchored to the header tier) is preserved.
- `issue-detail-reference-rail`: MODIFIED — pins the narrow-viewport collapse: metadata/configuration render as stacked expandable sections after the reading flow, low-frequency items (drift, convergence) collapsed by default, with the desktop right-column layout restored at `lg`+.

## Impact

- **Web** (`packages/web`):
  - `pages/issue-detail/ui/IssueDetailPage.tsx` — top-level layout gains a viewport-conditional split: narrow renders the bottom action bar + drawer and strips the `RuntimeDecisionSurface` from the header tier; desktop keeps the current header-tier action surface. Adds bottom padding to the scroll container.
  - `widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx` — destructive confirmation (stop, send-back) is refactored so the narrow path routes through the new bottom drawer while desktop keeps the existing inline confirmation; action rendering is split into a reusable primary-action source consumed by both the header surface (desktop) and the new bottom bar (narrow).
  - New components for the floating action bar and the bottom confirmation drawer (under `pages/issue-detail/ui/`), reusing `decision.primary` and the existing mutations — no new data source.
  - `shared/lib/use-narrow-viewport.ts` — consulted for the narrow/desktop split; note the breakpoint (`lg`, 1024px) is wider than the global `MobileBottomNav` visibility (`md`, 768px), so the bar's nav-offset must be conditional on the nav actually being present.
- **Server / runner / CLI**: none. No API, DTO, query, or projection changes; the adaptation consumes the existing `deriveRuntimeDecision` output (`decision.primary`, `stopRecoverable`, `approvalStage`) and existing mutations verbatim.
- **Dependencies**: none added.
- **Tests** (`packages/web`): new spec tests cover the floating bar (appears only when primary exists, no-overlap with bottom nav, bottom padding reserved), the confirmation drawer (slides from bottom, status headline remains visible, recoverable/irreversible copy), the narrow header read-only invariant, and the `lg`+ desktop restoration. The existing `IssueDetailPage.*.test.tsx` and `RuntimeDecisionSurface.test.tsx` suites are updated for the narrow/desktop split.
- **Risk (medium)**: viewport-conditional relocation of the action surface can regress the many conditional action paths (approval, failed/blocked, queued/backlog, running, done, archived). Mitigated by reusing the unchanged `decision.primary` derivation and existing mutations, and by asserting every runtime-summary path against both the narrow and desktop layouts.
