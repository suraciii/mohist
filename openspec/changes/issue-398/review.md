# Review Report

## Result: PASS

Independent, from-scratch review of the post-build candidate snapshot for
issue #398 (Make Web UI status and surfaces consistent across themes). The
change unifies ~15 Web UI status surfaces onto one token-backed
status-presentation layer with semantic `Badge`/`Button` variants and
dark-mode-aware registries. It is Web-only, builds, typechecks, and all tests
pass. No blocking defects were found. Several minor, cleanup, and
traceability items are listed below; none block merge.

### Review base (corrected and verified)

`master` and `origin/master` both point at `77245d7d9`. The candidate branch
forked at `3eb60175d`, so master has since advanced by one unrelated
server-side commit (`77245d7d9` — event-delivery progress / DLQ, touches only
`packages/server/` and the `issue-360` openspec archive; **no web files**).
The correct candidate diff is therefore `3eb60175d..HEAD` (merge-base diff).

Prior review iterations already removed the out-of-scope server/CLI/runner/
solution files and aligned the base; the reviewed snapshot confirms that
cleanup holds.

## Repaired Items

None. The candidate snapshot was already clean in the areas a previous
review repaired (scope/base), so no further in-review repair was warranted.
The remaining findings are either judgment calls on status surfaces (not
safe to auto-repair per policy) or documentation reconciliations.

## Blocking Items

None.

## Non-blocking Findings

- [ID: item-1]
  Severity: warning
  Scope: `openspec/changes/issue-398/design.md` (D6, lines ~300-305),
  `openspec/changes/issue-398/tasks.json` (T-004 acceptance criterion, line 86),
  vs `packages/web/src/widgets/kanban-board/model/stage-colors.ts:13-18`
  Evidence: The kanban `STAGE_FAMILY` mapping shipped as
  `InProgress → info` and `Cancelled → muted`, but design D6 and the T-004 AC
  both specify `InProgress → warning` and `Cancelled → danger`
  ("resolves Backlog/InProgress/Done/Cancelled to muted/warning/success/danger").
  The shipped mapping is the **more correct** choice: `info` keeps the
  InProgress column consistent with `issue-health.active` and
  `workflow-stage.running` (cross-surface equivalence, asserted at
  `cross-surface.equivalence.spec.tsx:373-378`), and `muted` keeps Cancelled
  consistent with `issue-health.cancelled` / `workflow-run` cancelled fallback
  (`cross-surface.equivalence.spec.tsx:384-386`). The design/AC wording is the
  stale side. [disallowed:reason] Reconciling a normative spec/AC with code is
  an architectural judgment (the design made a deliberate, if suboptimal,
  call), so it was not auto-repaired.
  SuggestedAction: Update design.md D6 and the T-004 acceptance criterion to
  `Backlog→muted, InProgress→info, Done→success, Cancelled→muted` so the
  artifacts match the shipped, equivalence-validated behavior.
  Verification: `rg "InProgress|Cancelled" openspec/changes/issue-398/design.md
  openspec/changes/issue-398/tasks.json` after reconciliation.
  Status: unresolved

- [ID: item-2]
  Severity: minor
  Scope: `packages/web/src/shared/status-presentation/contrast.spec.ts:14-21`
  and `packages/web/src/widgets/kanban-board/ui/StatusPill.contrast.test.ts:34-41`
  Evidence: The `muted` background/foreground pair (`MUTED_BG`/`MUTED_FG`) is
  hard-coded in both contrast tests and is **not** covered by
  `tokens.guard.test.ts`, which only parses the four semantic families. The
  comment at `contrast.spec.ts:11-12` claims the guard "verifies them
  implicitly", but it does not — `--muted`/`--muted-foreground` drift in
  `index.css` would leave the muted/cancelled WCAG assertions silently
  computing against stale values. Current values are correct (verified
  against `index.css:79-80,130-131`), so this is a robustness gap, not a
  current failure.
  SuggestedAction: Either extend the fixture/guard to cover the muted pair,
  or derive the muted values from a single shared constant imported by both
  contrast tests.
  Verification: Mutate `--muted-foreground` in `index.css` and confirm a test
  fails.
  Status: open

- [ID: item-3]
  Severity: minor
  Scope: `packages/web/src/shared/status-presentation/index.ts:131-162`
  vs `packages/web/src/shared/ui/components/badge.tsx:15-24`
  Evidence: `TREATMENT_BY_FAMILY.<family>.container` renders the text in the
  family **base** token (`text-success`, `text-warning`, …) while the `Badge`
  semantic variants render `text-<family>-foreground`. Today `--<family>` and
  `--<family>-foreground` are defined identically in `index.css:84-99` so the
  two paths produce the same color and the contrast fixture (which uses
  `base`) is accurate for both. But the layer and the primitive now read two
  different token names for the same slot — a latent drift seed in an issue
  whose entire purpose is to eliminate exactly this kind of divergence.
  SuggestedAction: Pick one (`-foreground` is the conventional shadcn choice)
  and use it in both the `TREATMENT_BY_FAMILY` container strings and the
  contrast fixture, or assert in the guard test that base === foreground.
  Verification: `rg "text-(success|warning|info|danger)\b" packages/web/src`.
  Status: open

- [ID: item-4]
  Severity: cleanup
  Scope: `packages/web/src/widgets/kanban-board/model/stage-colors.ts:30`
  Evidence: `const subtleBg = treatment.container.split(' ')[1]!` resolves the
  active background by **positional** parsing of the container class string
  (assumes index 0 is the `bg-<family>-subtle` utility). It works and is
  tested today, but a future reorder of `TREATMENT_BY_FAMILY` would silently
  break the active-column background. Reading the bg utility by name (or
  exposing a dedicated `bg` field on `StatusTreatment`) would be more robust.
  SuggestedAction: Expose `bg`/`subtle` as an explicit field on the treatment
  record instead of splitting the container string.
  Verification: `npm run test:run -w packages/web`.
  Status: open

- [ID: item-5]
  Severity: cleanup
  Scope: `packages/web/src/shared/ui/toast/RuntimeToastHost.tsx:237-241`
  Evidence: The toast container applies `t.container` (which already includes
  `border-<family>-border`) and then `t.border` (the same border class) again.
  Harmless duplicate; no visual effect.
  SuggestedAction: Drop the redundant `t.border` argument.
  Verification: `npm run test:run -w packages/web`.
  Status: open

- [ID: item-6]
  Severity: cleanup
  Scope: `packages/web/src/widgets/session-health/ui/ContextHealthIndicator.tsx:32-36`
  and `packages/web/src/widgets/session-health/ui/ContextHealthBar.tsx:32-36`
  Evidence: `HEALTH_TO_CONTEXT` is an identity map (`green→green`,
  `yellow→yellow`, `red→red`) in both components, so
  `statusTreatment('context-health', HEALTH_TO_CONTEXT[status])` is equivalent
  to passing `status` directly.
  SuggestedAction: Remove the identity map and pass `status` straight to
  `statusTreatment`, or document why the indirection is intentional.
  Verification: `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-7]
  Severity: follow-up
  Scope: `openspec/changes/issue-398/proposal.md:14`
  Evidence: The proposal lists "Make the theme selector reachable beyond
  Settings only where the milestone covers surfaces". No task references it,
  no design decision covers it, and no theme-selector code changed in the
  candidate (`git diff --name-only 3eb60175d..HEAD | rg -i theme` returns only
  the `theme-tokens` spec). It is an orphaned over-promise relative to what
  shipped. (Raised as self-review item-3; still open.) No issue #398
  acceptance criterion requires it, so it does not block.
  SuggestedAction: Product owner decides — either drop the line from the
  proposal, or open a follow-up issue with a real design decision + task.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `openspec/changes/issue-398/proposal.md:30`
  Evidence: The proposal Impact list names `shared/ui/ModelSelect.tsx` and
  "markdown-reader composites" as affected code (both still contain raw
  palette classes such as `border-blue-500`, `bg-amber-50`), but neither is
  status-bearing, neither has a task, and neither changed in this candidate.
  (Raised as self-review item-4; still open.)
  SuggestedAction: Trim the Impact list to match delivered scope, or open a
  separate issue if those surfaces are genuinely in scope.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:88`
  Evidence: When the only attention signal is a downed runner (`items` empty,
  `runnerDown` true), `attentionSummaryTreatment([])` resolves to the
  `warning` family, so the hero frame renders warning-colored around a single
  danger-colored `RunnerDownEntry`. This is **pre-existing** behavior — the
  pre-change `AttentionHero` hardcoded an amber (`border-amber-200
  bg-amber-50/60`) frame for all attention states regardless of runner-down —
  and this change preserves it (now token-backed and dark-aware). The danger
  signal itself remains prominent via the dedicated `RunnerDownEntry`. Not
  introduced by this change; flagged only for completeness.
  SuggestedAction: Optional future tightening — have `attentionSummaryTreatment`
  consider the runner-down signal when selecting the hero family.
  Status: pre-existing

## Acceptance-criteria evidence

Issue #398 acceptance criteria, each verified against the candidate:

1. **Consistent status across dashboard/board/issue-detail/activity/session/runner.**
   Every covered surface resolves through `statusTreatment(...)` /
   `familyFor(...)` and emits a `data-family` hook: `WorkflowRunStatusPill.tsx:46,52`,
   `StatusBar.tsx:22-25`, `IssueCard.tsx:120,131` (StatusPill) and `:155,164`
   (WorkflowStagePill), `RunnerList.tsx:31` / `RunnerSummary.tsx:43,66,89,112`,
   `ContextHealthIndicator.tsx:99,111` / `ContextHealthBar.tsx:69,140`,
   `RuntimeToastHost.tsx:70-83`, `log-levels.ts:13-18`, `attention-treatment.ts`.
   Cross-surface equivalence is asserted by spec at
   `cross-surface.equivalence.spec.tsx:159-387` (workflow-run, runner,
   issue-health, context-health, kanban, attention all-clear pairings).

2. **Dark mode has no light-only buttons/badges/panels.** The four semantic
   families all define `.dark` overrides (`index.css:135-150`); the
   `--warning` light hue drift is fixed (all `--warning*` now hue 75,
   `index.css:88-91`). Priority/area/urgency/categorical palettes all carry
   `dark:` counterparts (`label-colors.ts:47,149-190`,
   `CompactSessionCard.tsx:18-22`). The terminal body keeps a permanently-dark
   palette, explicitly allowed by design D7 / the spec.

3. **Status colors reserved for state meaning; distinguishable.** The
   reservation in `index.ts:42-111` pins one family per state; `success` is
   the only family for completed/done/idle/approved. WCAG AA (≥4.5:1) is
   asserted for every covered indicator in both themes
   (`contrast.spec.ts:82-144`, `StatusPill.contrast.test.ts:57-95`), computed
   from a JS fixture guarded against `index.css` drift
   (`tokens.guard.test.ts`).

4. **Primary/destructive/disabled/secondary consistent.** `Button`/`Badge`
   expose token-backed `success`/`warning`/`info`/`danger` variants;
   `destructive` is aliased to `danger` (`button.tsx:24-27`, `badge.tsx:20-23`);
   `AlertDialog` uses the variant alone with no raw-red override
   (`alert-dialog.tsx:48,70-79`); covered panel actions select color via
   variants only (`WorkspacePanel.tsx:245,266`, `BranchBar.tsx:109,151`,
   `TaskLogPanel.tsx:418,441`). Disabled treatment is uniform
   (`button.test.tsx:69-76`).

5. **Product terms preserved.** issue/workflow/stage/health/approval/runner/
   artifact/session/epic remain in copy and labels; ARIA contracts preserved
   (`role="alert"` / `role="status"` / `aria-live` on context-health, toasts,
   field-error: `ContextHealthIndicator.tsx:114-115`,
   `RuntimeToastHost.tsx:236`, `field-error.tsx:22`). `statusLabel()` is
   unchanged (`status-badge.tsx:17-33`).

## Verification

- Base/scope: `git diff --name-only 3eb60175d..HEAD | grep -vE '^(packages/web/|openspec/changes/issue-398/)'` → no output (candidate is Web + openspec only). `git diff --name-status 3eb60175d..master` shows the master-only commit touches only `packages/server/` and `openspec/changes/archive/.../issue-360/` — no web overlap, no merge conflict.
- Dangling refs: `rg -n "STATUS_PILL_PAIRS|STATUS_CONFIG|PRESENTATION_BY_STATUS" packages/web/src` → only a descriptive comment in `StatusPill.contrast.test.ts:12`; no live code.
- Raw-red overrides gone: `rg -n "bg-red-600|hover:bg-red-700|text-red-700" packages/web/src/shared/ui/components/{alert-dialog,field-error}.tsx` → no matches.
- `npm run typecheck -w packages/web` → `tsc -b` clean.
- `npm run test:run -w packages/web` → 306 files, **4650 passed | 1 skipped**.
- `npm run build -w packages/web` → **built in 4.52s** (3486 modules transformed).

<promise>PASS</promise>
