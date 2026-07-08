# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/runner-status/ui/RunnerSummary.tsx`, `packages/web/src/shared/status-presentation/cross-surface.equivalence.spec.tsx`
  Resolution: `RunnerSummary` now preserves stale and offline as separate states, so offline-only summaries render with the muted runner family instead of the stale warning family. The cross-surface spec now requires `RunnerList` and `RunnerSummary` to match `familyFor('runner', state)` for every runner state.
  Status: fixed

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/widgets/kanban-board/model/stage-colors.ts`, `packages/web/src/shared/status-presentation/cross-surface.equivalence.spec.tsx`
  Resolution: The cancelled kanban stage now resolves to the muted family, matching cancelled issue-health surfaces. Unit and cross-surface tests now lock this mapping.
  Status: fixed

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`
  Resolution: `NeedsAttentionSummary` and `RunnerUnavailableBanner` now render through semantic warning treatments instead of raw `amber-*` and `bg-white` classes, with tests asserting the warning family and token classes.
  Status: fixed

## Blocking Items

- None.

## Follow-up Items

- None.

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 306 files passed, 4649 tests passed, 1 skipped.

<promise>PASS</promise>
