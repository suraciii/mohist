# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/kanban-board/model/stage-colors.ts`, `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx`
  Evidence: The issue and spec require the same domain state to render consistently across covered surfaces, and explicitly call out completed workflow / done issue / dashboard completed as all needing the `success` family, not mixed hues. The current implementation hard-codes kanban `IssueStatus.InProgress` to `warning` in [`stage-colors.ts`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/widgets/kanban-board/model/stage-colors.ts:41), and [`IssueCard.tsx`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:146) renders any non-done workflow stage pill from that map. As a result, an in-progress workflow stage on the board renders with `warning`, while the shared status layer maps workflow-stage `running` to `info` in [`shared/status-presentation/index.ts`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/shared/status-presentation/index.ts:64). That reintroduces exactly the same-state cross-surface divergence this issue was meant to remove.
  SuggestedAction: Route workflow stage presentation through the shared status layer for actual stage state, or narrow the kanban accent palette so it is clearly categorical and not used for status-bearing workflow stage pills.
  Verification: Render the same running workflow stage on board, issue detail, and dashboard surfaces and verify each resolves to the same semantic family.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-event-timeline/model/types.ts`, `packages/web/src/widgets/issue-event-timeline/ui/CategoryFilter.tsx`
  Evidence: The acceptance criteria require the same issue health, workflow stage, approval state, and runner state to be represented consistently on activity surfaces, and the spec requires event severity coloring used by the issue-event-timeline to route through tokens. The current timeline model only routes `approval` and `failure` through semantic treatments. `success`, `workflow`, `integration`, and `metadata` are forced back to neutral gray in [`types.ts`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/widgets/issue-event-timeline/model/types.ts:38), and [`CategoryFilter.tsx`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/widgets/issue-event-timeline/ui/CategoryFilter.tsx:21) only treats `approval` and `failure` as semantic categories. That means success-severity activity items on the Activity surface still do not use the shared status language and are visually collapsed into neutral metadata.
  SuggestedAction: Map success-bearing timeline categories through the shared status layer as well, and update the filter chip logic so every severity-bearing category uses the corresponding semantic family instead of the neutral fallback.
  Verification: Inspect the activity timeline and category filter for success, approval, warning, and failure items and confirm each category resolves to its intended family from the shared layer.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/web/src/shared/ui/components/badge.tsx`, `packages/web/src/shared/ui/components/button.tsx`, `packages/web/src/shared/status-presentation/StatusPill.tsx`
  Evidence: The approved design and tasks required the new semantic `Badge` and `Button` variants to use `bg-<family>-subtle text-<family>-foreground border-<family>-border`. Instead, both primitives now use `text-success`, `text-warning`, `text-info`, and `text-danger` in [`badge.tsx`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/shared/ui/components/badge.tsx:15) and [`button.tsx`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/shared/ui/components/button.tsx:18), and [`StatusPill.tsx`](/home/szf/.mohist/projects/mohist-local/workspaces/issue-398/packages/web/src/shared/status-presentation/StatusPill.tsx:32) explicitly abandons the `Badge` primitive because its variants no longer match the spec shape. This is not just a documentation mismatch: the implementation leaves the shared primitive contract different from the promised contract, and the issue no longer delivers the designed reusable baseline that other pages were supposed to build on.
  SuggestedAction: Either bring the primitive contract back in line with the approved spec/design, or update the spec/design/tasks before merge so the delivered contract is intentionally different and downstream work is not built on false assumptions.
  Verification: Check the primitive class sets and confirm the shipped contract matches the documented semantic-primitive requirement used by later tasks.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/widgets/runner-status/ui/RunnerList.tsx`
  Evidence: `CapacityIndicator` still picks `success`/`info`/`warning` with local conditional logic and local `bg-*` selection instead of resolving through the shared layer. This does not clearly violate the issue because capacity saturation is not one of the named domain-state surfaces, but it is another local status-like mapping adjacent to the reviewed changes.
  SuggestedAction: Consider routing capacity presentation through a small shared helper or documenting it as intentionally separate from state meaning.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: verification
  Evidence: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` both passed on the reviewed snapshot. The failures above are behavioral/spec-compliance review findings rather than red-test failures.
  SuggestedAction: Keep the same commands in the fix cycle.
  Status: out-of-scope

<promise>FAIL</promise>
