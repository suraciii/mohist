# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/pages/session/ui/SessionDetailShell.tsx
  Evidence: The cancel confirmation dialog is never closed by the confirm path. `AlertDialog` only calls `onConfirm()` and does not close itself (`packages/web/src/shared/ui/components/alert-dialog.tsx:39-47`), while the new handler only calls `cancel.mutate()` (`packages/web/src/pages/session/ui/SessionDetailShell.tsx:535-549`) and never clears `cancelDialogOpen`. For the normal best-effort response `{ state: "cancelled" }`, the summary can still remain running until the agent later emits a terminal event, so `showCancelControl` remains true and the dialog stays open with the confirm button re-enabled after the mutation settles. The user can submit duplicate cancel POSTs and the UI looks stuck after a successful notification. [disallowed:product-behavior]
  SuggestedAction: Close the dialog from the cancel mutation lifecycle, for example by allowing the data-source cancel callback to accept per-call mutation options and calling `setCancelDialogOpen(false)` on success/settled, while preserving the loading guard during the in-flight request. Add a regression test that confirms the dialog closes after the cancel mutation settles while the session is still non-terminal.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 264 files, 4176 tests passed, 1 skipped. These tests do not cover post-confirm dialog closure.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/web/src/pages/session/data/useGenericSessionDataSource.ts
  Evidence: The generic session summary has the owning agent id (`summary.agentId`), and `useCancelGenericSession` already invalidates the owning agent session list when `agentRef` is provided (`packages/web/src/entities/agent/api/agent-sessions.ts:205-207`). The new page wrapper discards that available id and calls `cancelGeneric.mutate({ sessionId })` only (`packages/web/src/pages/session/data/useGenericSessionDataSource.ts:114-116`). `AgentDetailPage` reads the affected cache with query key `['agents', projectId, agentRef, 'sessions']` (`packages/web/src/entities/agent/api/queries.ts:137-143`) and has no refetch interval, so returning to the agent detail page after cancelling can show the old running session list indefinitely until another invalidation or remount/refetch path occurs. [disallowed:product-behavior]
  SuggestedAction: Pass the owning agent reference from the loaded summary when cancelling, e.g. `cancelGeneric.mutate({ sessionId, agentRef: summary?.agentId })`, and add a data-source/page test that the cancel mutation receives `agentRef` once the summary is available.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 264 files, 4176 tests passed, 1 skipped. Existing hook tests prove the invalidation works only when `agentRef` is supplied; the new UI path does not supply it.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: packages/web/src/pages/session/ui/GenericSessionPage.test.tsx
  Evidence: The issue/spec acceptance criteria explicitly require the cancel control to be visible for `active`, `running`, and `probing` generic sessions, and hidden/disabled for `completed`, `failed`, `cancelled`, and `stopped` terminal sessions (`openspec/changes/issue-349/specs/generic-agent-session-cancel/spec.md:23-32`). The new tests cover `running`, `completed`, `failed`, and `cancelled` only (`packages/web/src/pages/session/ui/GenericSessionPage.test.tsx:300-338`). They do not cover `active`, `probing`, or `stopped`, so three acceptance-state branches are unverified in the product UI test suite. [disallowed:test-coverage]
  SuggestedAction: Add parameterized visibility tests for non-terminal statuses `active`, `running`, and `probing`, and terminal statuses `completed`, `failed`, `cancelled`, and `stopped`.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 264 files, 4176 tests passed, 1 skipped. The passing suite lacks the missing status cases above.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: packages/web/src/entities/agent/api/agent-sessions.ts
  Evidence: `useGenericSessionSummary` stops polling only for `completed`, `failed`, and `stopped` (`packages/web/src/entities/agent/api/agent-sessions.ts:127-131`), but other code and the new issue acceptance treat `cancelled` as terminal. This was not introduced by the UI wiring, and the new cancel button still hides for cancelled because `isRunning` becomes false, but cancelled summaries may continue polling unnecessarily.
  SuggestedAction: In a separate cleanup, include `cancelled` in the generic-session summary terminal polling predicate and add a unit test.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: openspec/changes/issue-349
  Evidence: Workflow artifacts (`proposal.md`, `design.md`, `tasks.json`, `self-review.md`, spec delta, and this review) are present as expected review context under the issue change directory. Per the candidate boundary, these are not product deliverables and do not affect the verdict by themselves.
  SuggestedAction: No action.
  Status: out-of-scope

<promise>FAIL</promise>
