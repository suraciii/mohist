## Why

The Web UI exposes the same production state (issue health, workflow stage, approval, runner state) through several parallel color systems that disagree with each other and ignore dark mode. A rich semantic status palette (`success`/`warning`/`info`/`danger`, each with `-subtle`/`-border`/`-foreground`) already exists in the theme and is proven by a few reference components, but the majority of status surfaces still hardcode raw Tailwind palette colors (e.g. `bg-amber-100`, `bg-emerald-500`, inline `#ef4444`) that do not switch between light and dark and that map the *same* concept to *different* hues across pages. This makes the UI look inconsistent page-to-page, weakens or hides blocking/approval signals in dark mode, and is the visual baseline the rest of the epic (dashboard, board, issue detail, activity, session) depends on — so it must land first.

## What Changes

- Introduce **one shared status-presentation layer** that maps each domain state (running, awaiting approval, blocked, interrupted, drift, done, cancelled, failed, unknown; runner idle/busy/stale/offline; issue health active/paused/blocked/interrupted) to a single semantic-token treatment, and route every status pill, badge, dot, and marker through it.
- Route all status pills, health badges, runner dots, and stage indicators through the existing semantic tokens (`bg-*-subtle text-* border-*-border`) instead of hardcoded Tailwind hues; keep "completed/done" to one hue family (`success`) instead of the current `green` vs `emerald` split.
- Extend the `Badge` and `Button` primitives with `success`/`warning`/`info`/`danger` variants (soft-tinted, token-backed) so status surfaces and semantic actions stop hand-rolling colors.
- Reserve status colors strictly for state meaning: running→info, awaiting-approval/blocked→warning/danger, interrupted/drift→warning/danger, done→success, failed→danger, unknown/cancelled/paused→muted. Two states never share a color unless they share a meaning.
- Move priority, risk, label, and stage-accent colors off inline hex and onto tokens (or a documented light/dark-aware palette) so they survive dark mode.
- Remove the parallel/`STATUS_PILL_PAIRS` dead color maps and the divergent `statusBadge()`/`STATUS_CONFIG` helpers; collapse to the shared layer.
- Standardize **action button** treatment: primary (solid `primary`), destructive (token `destructive`), secondary/outline, disabled — all via Button variants; remove bespoke `border-slate-300 bg-white` / `border-amber-300` action styles from `WorkspacePanel`, `TaskLogPanel`, `BranchBar`, `alert-dialog`.
- Make the theme selector reachable beyond Settings only where the milestone covers surfaces (no new dependency, no new full-page redesign).
- Keep existing product terms intact: issue, workflow, stage, health, approval, runner, artifact, session, epic.

Non-goals (per issue): no full-page redesign of dashboard/board/issue-detail/activity/session/files/diff; no workflow/issue/runner/epic/approval semantic change; no decorative skin; no new design-system dependency. Diff added/removed-line coloring (green/red) stays as the conventional line-level convention and is not recolored.

## Capabilities

- `status-presentation`: A single shared mapping from domain state (issue health, workflow run/stage status, approval state, runner state, severity) to a semantic-token visual treatment (badge/pill/dot/marker), plus the primitives that expose it. This is the contract every status surface will consume.
- `semantic-primitives`: Extending `Badge` and `Button` with `success`/`warning`/`info`/`danger` token-backed variants and a consistent destructive/primary/secondary/disabled action treatment so status and action styling is expressed through primitives rather than ad-hoc classes.
- `theme-tokens`: Extending the design-token set and the shared color registries (priority, risk, label, stage accent) so they are dark-mode-aware and free of inline hex, covering the production surfaces in this milestone.
- `status-surface-consistency`: Applying the shared status-presentation layer across the covered surfaces (dashboard, board, issue detail, activity, session, runner) so the same state renders identically and dark mode has no light-only combinations on those surfaces.

## Impact

- **Affected code (Web only, `packages/web/src`):**
  - Shared primitives: `shared/ui/components/badge.tsx`, `button.tsx` (new variants); `shared/ui/components/alert-dialog.tsx`, `field-error.tsx` (stop hardcoding red).
  - Shared libs/composites: `shared/lib/label-colors.ts` (priority/risk/label hex), `shared/lib/log-levels.ts`, `shared/ui/StatusBar.tsx`, `shared/ui/toast/RuntimeToastHost.tsx`, `shared/ui/ModelSelect.tsx`, markdown-reader composites.
  - New shared status layer (e.g. `shared/status/` or `entities/.../status-presentation`) replacing `entities/issue/lib/status-badge.ts`.
  - Widgets that own status rendering: `issue-workflow/ui/WorkflowRunStatusPill.tsx`, `StageStatusIcons.tsx` (reference, keep), `TaskProgressPanel.tsx`, `ReviewReportModal.tsx`, `ReviewSummary.tsx`; `kanban-board/ui/IssueCard.tsx` (`StatusPill`, `STATUS_PILL_PAIRS`), `model/stage-colors.ts`; `runner-status/ui/RunnerList.tsx`, `RunnerSummary.tsx`; `dashboard-pulse/ui/CompactSessionCard.tsx`; `session-health/ui/ContextHealthIndicator.tsx`, `ContextHealthBar.tsx`; `attention-hero/ui/AttentionHero.tsx`; `issue-event-timeline` row/marker coloring.
  - Bespoke action-button styling in `workspace/ui/WorkspacePanel.tsx`, `issue-workflow/ui/TaskLogPanel.tsx`, `issue-workflow/ui/BranchBar.tsx`.
- **Theme tokens:** `app/styles/index.css` — extend (if needed) and ensure the `success`/`warning`/`info`/`danger` families remain the single source of truth; no new dependency.
- **Tests:** update/replace existing contrast tests (e.g. `StatusPill.contrast.test.ts`) to assert against the shared layer; add spec tests that the same domain state renders the same treatment across the covered surfaces and that dark mode has no light-only status combinations.
- **APIs / dependencies / domain behavior:** none changed — purely presentation. No server, runner, or CLI impact. No new design-system dependency.
- **Risk:** touches shared presentation across many surfaces; regressions can cascade visually even though domain behavior is unchanged. Mitigated by routing through one shared layer and by keeping diff/session-transcript line coloring out of scope.
