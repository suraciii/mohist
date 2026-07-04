# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/milestones.ts`
  Evidence: The issue acceptance criteria and task-log spec require milestone rows to be mixed with ops rows by time. The spec says the task log panel SHALL render milestone rows "interleaved and sorted by time" with ops lines (`openspec/changes/issue-339/specs/task-log-viewer/spec.md:1-8`). The implementation instead sorts ops rows by `seq` first and only inserts milestones while walking that seq-ordered stream (`packages/web/src/widgets/issue-workflow/ui/milestones.ts:84-99`). The candidate even locks in the non-time-sorted behavior with a test where `seq: 1` has timestamp `10:05`, `seq: 2` has timestamp `10:00`, and the later timestamp remains before the earlier timestamp (`packages/web/src/widgets/issue-workflow/ui/milestones.test.ts:237-249`; `TaskLogPanel.test.tsx:1488-1533`). This violates the post-build candidate's stated contract for edge cases where line timestamps and seq order disagree. [disallowed:product-behavior-change]
  SuggestedAction: Make the merged timeline globally honor timestamp ordering, with an explicit stable tie-breaker for equal timestamps, or revise the accepted spec/issue contract if ops `seq` order is the intended primary ordering.
  Verification: Add or update a test where ops line timestamps disagree with `seq` and assert the rendered rows/download export match the accepted ordering; rerun `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx`
  Evidence: For an agent task with no ops lines, `renderScrollBody` can show the "No execution log captured" empty state while the workflow-run sessions query is still loading because the branch only checks task-log loading (`isLoading`) and `lines.length === 0 && milestones.length === 0` (`TaskLogPanel.tsx:317-330`). This is a transient UX issue rather than a functional failure once the session summary arrives.
  SuggestedAction: Consider also accounting for session-query loading for eligible agent tasks before showing the true-empty copy.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/milestones.ts`
  Evidence: The predicate requires non-empty `classification` (`milestones.ts:30-35`), while the executable task acceptance criterion describes `isAcpAgentTask` as true when `origin.uses === 'mohist/acp-agent'` and `sessionName` is non-empty (`openspec/changes/issue-339/tasks.json:12`). The server currently projects classification with a default, so this is unlikely to affect normal current tasks, but the artifact wording and test expectation diverge.
  SuggestedAction: Clarify whether `classification` is only a retained/context field or a required predicate input, then align the spec/task/tests.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/tests/a11y/settings.a11y.spec.ts`
  Evidence: `npm run test:a11y -w packages/web` failed in unchanged Settings Playwright audits, not in the task-log milestone coverage. Failures include serious color-contrast violations on `.text-primary/80` in Settings light routes and `.text-red-700` in the Settings repositories dark route, plus two Settings workflow/profile selector timeouts. The changed task-log a11y file passes when run directly: `npx vitest run tests/a11y/task-log-a11y.test.tsx --config vitest.a11y.config.ts`.
  SuggestedAction: Track/fix the Settings a11y failures separately, or quarantine known pre-existing Playwright failures if they are already tracked.
  Status: out-of-scope

<promise>FAIL</promise>
