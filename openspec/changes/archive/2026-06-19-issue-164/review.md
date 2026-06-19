# Review Report

## Result: PASS

## Repaired Items

(none)

## Blocking Items

(none)

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: packages/web/src/entities/issue/model/attention.ts
  Evidence: Acceptance criterion 1 is satisfied: `deriveAttentionItems`, `isIntegrateFailure`, and `AttentionItem` now live in the shared Issue-context module at `packages/web/src/entities/issue/model/attention.ts:4`, `packages/web/src/entities/issue/model/attention.ts:11`, and `packages/web/src/entities/issue/model/attention.ts:21`; the previous widget-local module is removed from the candidate as a rename from `packages/web/src/widgets/kanban-board/model/homepage-attention.ts` to `packages/web/src/entities/issue/model/attention.ts`.
  SuggestedAction: No action required.
  Status: out-of-scope

- [ID: item-2]
  Severity: info
  Scope: packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx
  Evidence: Acceptance criterion 2 is satisfied: `KanbanBoard` imports `deriveAttentionItems` from the Issue entity public API at `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx:8` and calls it with the same `(issues, agentStatus)` inputs at `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx:541`. `grep` over `packages/web/src` found no remaining `homepage-attention` or `kanban-board/model/homepage-attention` references.
  SuggestedAction: No action required.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: packages/web/src/entities/issue/index.ts
  Evidence: Acceptance criterion 3 is satisfied: the Issue entity public API exports `deriveAttentionItems`, `isIntegrateFailure`, and `AttentionItem` at `packages/web/src/entities/issue/index.ts:9`, making the same derivation available to future Dashboard consumers without importing from the Kanban widget.
  SuggestedAction: No action required.
  Status: out-of-scope

- [ID: item-4]
  Severity: info
  Scope: packages/web/src/entities/issue/model/attention.ts
  Evidence: Acceptance criterion 4 is satisfied: the shared derivation preserves the existing four rules and `AttentionItem` shape (`issueNumber`, `issueId`, `label`, optional `detail`) at `packages/web/src/entities/issue/model/attention.ts:4`; rule order remains approval awaiting, integrate failure, interrupted, blocked at `packages/web/src/entities/issue/model/attention.ts:28`, `packages/web/src/entities/issue/model/attention.ts:36`, `packages/web/src/entities/issue/model/attention.ts:44`, and `packages/web/src/entities/issue/model/attention.ts:52`. No files under `packages/server/` are changed in `git diff --name-status master...HEAD`.
  SuggestedAction: No action required.
  Status: out-of-scope

- [ID: item-5]
  Severity: info
  Scope: validation
  Evidence: Focused regression coverage is present in `packages/web/src/entities/issue/model/attention.test.ts:37` through `packages/web/src/entities/issue/model/attention.test.ts:418`, covering integrate failure, approval, interrupted, blocked fallback, first-match wins, duplicate id deduplication, healthy input, and the retained `AgentStatus` parameter. `npm test -- --run src/entities/issue/model/attention.test.ts` passed 22 tests, and `npm run build` in `packages/web` completed `tsc -b && vite build` successfully. The build emitted only third-party Rollup annotation warnings from `@microsoft/signalr`, with no failure.
  SuggestedAction: No action required.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: packages/runner/tests
  Evidence: The prior review's blocking scope issue is resolved: `git diff --name-only master...HEAD -- packages/runner/tests` produced no output, so the unrelated runner test changes are no longer part of the post-repair candidate snapshot.
  SuggestedAction: No action required.
  Status: out-of-scope

<promise>PASS</promise>
