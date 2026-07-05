## Why

The issue-workflow widget concentrates two high-density code spots that fight the
directory's own conventions: a 1455-line `WorkflowView` god component (29 inline
helpers, icons, and subcomponents in one file) and a 673-line
`derive-runtime-decision` whose four presentation builders each walk every
`RuntimeSummary` state via `if`-chains. Adding a run-state display or adjusting
one summary's copy forces scattered edits across these monoliths, while every
neighboring file (`TaskProgressPanel`, `WorkflowSessionsPanel`,
`RuntimeDecisionSurface`) already ships one panel per file. This refactor pays
down the density so future presentation changes land in one focused place and
stop forcing readers to grep a single huge file.

## What Changes

- Split `ui/WorkflowView.tsx` into per-file modules matching the existing
  "one panel per file" `ui/` convention:
  - `ui/format.ts` — pure helpers (`classifyResult`, `formatDuration`,
    `formatClock`, `parseTimelineTaskOutput`, origin-label formatters).
  - `ui/StageStatusIcons.tsx` — the six status icons + `StageStatusIcon`
    dispatcher.
  - `ui/StageBar.tsx` — `StageBar` + stage helpers (`getStageStatus`,
    `getStageDuration`, `workflowTimelineToStageStateMap`, `StageBarCell`).
  - `ui/TaskItem.tsx` — `TaskItem` + `TaskLifecycleTime`, `RunningElapsed`,
    `TaskSessionChip`, `TaskArtifactSummaryChip`, `RequiredFileEntry`.
  - `ui/CheckItem.tsx` — `CheckItem` + `CheckRepairPanel`.
  - `ui/InlineApproval.tsx` — `InlineApprovalControls` + `StepList`.
  - `ui/failure-panels.tsx` — `DeliveryFailureBanner`, `IntegrateFailurePanel`,
    `SpecialStatePanel`.
  - `ui/WorkflowView.tsx` (slimmed) — composition/assembly only.
- Reorganize `model/derive-runtime-decision.ts` presentation builders
  (`buildHeadline` / `buildRationale` / `buildNextAction` / `buildActions`) from
  **per-builder** `if (summary === …)` chains into a **per-summary** structure
  (e.g. `Record<RuntimeSummary, { headline, rationale, nextAction, actions }>`)
  so one state's full copy + action set co-resides in one place.
- Extract the shared pure input-query helpers (`findRunningTask`,
  `findFailedCheck`, `findRunningCheck`, `isScriptHealthCheck`,
  `formatStageLabel`) into a reusable module, eliminating the duplication
  between WorkflowView and the decision derivation.
- Preserve all output values verbatim: no new or removed `RuntimeSummary` enum
  values, no changes to the available action set or approval/retry behavior, no
  visual layout changes.
- Widget barrel public surface (`index.ts`) unchanged — external consumers
  require zero edits.

## Capabilities

- `issue-workflow-view`: Visual rendering of the workflow stage bar, task/check
  lists, inline approval controls, delivery/integrate failure panels, and
  special-state (backlog / blocked / interrupted) panels. DOM structure, copy,
  and interactive behavior are preserved byte-for-byte; only file boundaries
  move.
- `runtime-decision-derivation`: Classification of runtime state into a
  `RuntimeSummary` and projection of `headline` / `rationale` / `nextAction` /
  `actions` for the runtime decision surface. The output contract is preserved;
  presentation is reorganized from the per-builder dimension to the per-summary
  dimension, and shared query helpers become reusable.

## Impact

- **Code**:
  - `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx` shrinks from
    1455 lines to a composition-only entry; seven new sibling files land under
    `ui/`.
  - `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`
    (673 lines) is restructured into per-summary presentation (likely a
    presentation module + a shared query-helpers module).
- **Tests**: `ui/WorkflowView.test.tsx` (774 lines) and
  `model/derive-runtime-decision.test.ts` (734 lines) are the regression guards
  and must pass unchanged — no new public behavior is added to cover.
- **APIs / Dependencies**: None. No type, barrel, runtime, or data-structure
  changes; `index.ts` exports are identical.
- **Risk**: Medium. The decision-derivation restructure is the largest
  logic-equivalence risk; it is staged after the zero-risk pure-helper and
  component extractions, with `npm run typecheck -w packages/web` and
  `npm run test:run -w packages/web` run after each step.
