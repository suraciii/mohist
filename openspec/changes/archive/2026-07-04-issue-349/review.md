# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: openspec/changes/issue-349/progress.txt
  Evidence: The product snapshot now covers the prior review gaps, but `progress.txt` still contains stale progress evidence: it says `GenericSessionPage.test.tsx` added visibility coverage only for `running` vs `completed` / `failed` / `cancelled` and omits the now-present `active`, `probing`, `stopped`, and post-settlement dialog-close assertions (`progress.txt:92-100`; current tests are in `packages/web/src/pages/session/ui/GenericSessionPage.test.tsx:300-437`). This does not affect the product deliverable or verdict because the current tests and review evidence are authoritative, but stale handoff notes can confuse future traceability.
  SuggestedAction: Refresh `progress.txt` if it will be used as handoff evidence after this review.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: packages/web/src/pages/session/data/buildGenericSessionMetadata.ts
  Evidence: Generic session status presentation still maps raw `cancelled` to the existing failed badge and raw `stopped` through the existing non-running fallback to completed (`buildGenericSessionMetadata.ts:4-17`). This predates the cancel-button wiring and does not block the issue acceptance criteria, which are about providing the cancel affordance, confirming it, invoking the existing endpoint, refreshing state, hiding the control for terminal states, and preserving issue/workflow session pages.
  SuggestedAction: Consider a separate UI vocabulary cleanup if stopped/cancelled generic sessions need distinct labels.
  Status: pre-existing

- [ID: item-3]
  Severity: info
  Scope: openspec/changes/issue-349
  Evidence: Workflow artifacts (`proposal.md`, `design.md`, `tasks.json`, `self-review.md`, spec delta, and this review) are present under the issue change directory as expected review context. Per the candidate boundary, these are not product deliverables by themselves and did not affect the verdict.
  SuggestedAction: No action.
  Status: out-of-scope

## Acceptance Evidence

- Generic session header cancel affordance is implemented in `packages/web/src/pages/session/ui/SessionDetailShell.tsx:518-550`, gated by `cancel != null && isRunning` (`SessionDetailShell.tsx:404-406`) and supplied only by `useGenericSessionDataSource` (`packages/web/src/pages/session/data/useGenericSessionDataSource.ts:114-123`).
- Confirmation is destructive-toned and does not invoke the mutation until confirm (`SessionDetailShell.tsx:534-550`; shared loading/dismiss guard in `packages/web/src/shared/ui/components/alert-dialog.tsx:39-47`). Tests cover open-without-request, dismiss-without-request, confirm, pending UI, and post-settlement dialog close (`GenericSessionPage.test.tsx:338-437`).
- Terminal hiding and non-terminal visibility are covered for `active`, `running`, `probing`, `completed`, `failed`, `cancelled`, and `stopped` in `GenericSessionPage.test.tsx:300-323`; summary polling also treats `cancelled` as terminal in `packages/web/src/entities/agent/api/agent-sessions.ts:127-131`.
- The existing cancel endpoint is called through `cancelGenericSession` (`agent-sessions.ts:110-116`) and the wrapper passes the owning `agentRef` for list invalidation (`useGenericSessionDataSource.ts:114-119`). Hook tests cover success, terminal, `not-cancellable`, and error toast paths (`agent-sessions.test.ts:336-393`).
- Issue/workflow sessions remain structurally unable to render cancel because `useIssueSessionDataSource` returns `cancel: null` (`packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:245-260`), with regression coverage in `packages/web/src/pages/session/ui/SessionPage.cancel.test.tsx:252-266`. Backend/runner isolation remains pre-existing and unchanged (`AgentSessionCancelRoutes.cs:44-105`; `runner-signalr.ts:561-621`).

## Verification

- `git diff --check origin/master...HEAD` passed.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web -- src/entities/agent/api/agent-sessions.test.ts src/pages/session/ui/GenericSessionPage.test.tsx src/pages/session/ui/SessionPage.cancel.test.tsx` passed: 3 files, 61 tests.
- `npm run test:run -w packages/web` passed: 264 files, 4183 passed, 1 skipped.

<promise>PASS</promise>
