# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`, `packages/web/src/widgets/dashboard-pulse/ui/PulseZone.tsx`
  Evidence: Active sessions without an `InProgress` issue are hidden from the first screen. `DashboardPage` computes active work only as `runningIssues.length > 0` (`DashboardPage.tsx:62-63`), and `PulseZone` filters its rows only from `issues.filter(isRunningIssue)` (`PulseZone.tsx:39-48`). The candidate also locks in the opposite of the task requirement with `DashboardPage.test.tsx:648-662`, which asserts an active card without a running issue must not render the active-production zone. This contradicts `tasks.json` T-003 acceptance criteria that `hasActiveWork` is true for "running issues or active sessions" and the issue's product shape that active production includes current sessions. [disallowed:product-behavior-change]
  SuggestedAction: Make the active-production decision and rendered rows account for active sessions as well as in-progress issues, or explicitly revise the spec/task if active-session-only work is intentionally no longer dashboard content. Replace the inverted test with coverage that an active session remains visible and does not trigger the ready state.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 307 files, 4713 tests passed, 1 skipped. The passing suite includes the inverted assertion, so tests do not validate the required behavior.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/entities/issue/model/attention.ts`
  Evidence: `issueNeedsOwnerAction(issue)` is not the shared predicate used by `deriveAttentionItems`; it is a separate duplicate of the same conditions. The predicate is defined at `attention.ts:35-41`, while `deriveAttentionItems` repeats the classification chain at `attention.ts:50-77` instead of delegating to the shared predicate or a shared classifier. This does not satisfy T-002 acceptance criterion "issueNeedsOwnerAction(issue) is a single shared predicate used by both the inline cue and deriveAttentionItems," so the cue and attention entry can drift despite the current lock-step tests. [disallowed:architectural-judgment]
  SuggestedAction: Extract one issue-attention classifier/predicate path and use it from both `deriveAttentionItems` and the Pulse owner-action cue. Keep the existing classification order and add a regression test around the shared classifier rather than two duplicated condition chains.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but the tests only compare duplicated behavior and do not prove single-source implementation.
  Status: open

- [ID: item-3]
  Severity: cleanup
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`, `packages/web/src/widgets/dashboard-pulse/ui/PulseZone.tsx`, `packages/web/src/widgets/factory-status/model/factory-status.ts`
  Evidence: The running-issue predicate is duplicated across dashboard surfaces instead of being shared. `DashboardPage.tsx:34-40` and `PulseZone.tsx:10-16` each define a private `isRunningIssue`, while `factory-status.ts:33-36` carries the same in-flight rule inline. T-003 acceptance says `hasAttention` and `hasActiveWork` are computed from the same predicates/hooks used by the hero and pulse; the current snapshot relies on copy/paste equivalence, so future changes can desynchronize the zone gate, Pulse rows, and headline count. [disallowed:architectural-judgment]
  SuggestedAction: Move the running-issue predicate to a shared dashboard/entity model location and use it from the headline, page gate, and Pulse zone.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but there is no guard against predicate drift between these three copies.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: branch integration state
  Evidence: `git status -sb` reports this branch is ahead of `origin/master` by 9 commits and behind by 1 commit. `git log HEAD..origin/master` shows the missing upstream commit is `ba50c2089 test(server): 测试组织重构——纯 unit 迁入 UnitTests，Speed=Unit trait 正本清源`, which is outside the issue-399 Web dashboard deliverable.
  SuggestedAction: Rebase or merge the upstream branch before integration if the workflow does not do that automatically.
  Status: out-of-scope

<promise>FAIL</promise>
