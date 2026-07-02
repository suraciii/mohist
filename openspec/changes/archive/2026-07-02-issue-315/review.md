# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dead-code-removal
  Evidence: `reverse-dns-outcome.ts:130-136` exported `asRebaseDispatchEvent` — a type-narrowing helper with docblock claiming it exists "for callers that need to branch on the rebase event shape." No caller in the entire codebase imports or uses it. The only consumer of `ReverseDnsRebaseEvent` is `applyReverseDnsOutcome` in `handle-event.ts`, which passes it directly to `dispatchRebaseEvent` without the helper. After removing the unused export, the `RebaseEvent` import from `rebase-events` became dead and was also removed.
  Verification: `npm run typecheck -w packages/web` (clean), `npm run test:run -w packages/web -- src/app/providers` (70 tests passing, including 26 reverse-dns-outcome unit tests). Grep across `*.ts` and `*.tsx` confirms zero references to `asRebaseDispatchEvent` post-removal.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `handle-event.ts:267-273`, `LiveTaskProvider.tsx` (removed guard)
  Evidence: The old compile-time guard (`_AssertEventNameSubscribed`) checked `Exclude<EventName, (typeof EVENT_TYPES)[number]>` — ensuring every known event name (including agent-detail and transcript names) must be in the subscription set. The new guard (`_AssertRouteSubscribes` at `handle-event.ts:267`) only checks `Exclude<keyof typeof ROUTE, (typeof EVENT_TYPES)[number]>` — ensuring only routable names must be subscribed. This narrowing is intentional (design D6 / progress.txt post-T-005: "AgentSessionContextCompacted... are NOT in ROUTE — and that's correct, because the provider doesn't route them") and all current EventName values are in EVENT_TYPES, so the guard passes in both forms. However, the narrowed guard would not catch a future addition to `EventName` (via `AgentDetailEventMap`) that is missing from `EVENT_TYPES` — unlike the original guard which checked the full union.
  SuggestedAction: Consider adding a second compile-time guard back in `LiveTaskProvider.tsx` that checks the full `Exclude<EventName, (typeof EVENT_TYPES)[number]>` to restore the "every known event name is subscribed" invariant, or document in `conventions.md` that new event names must be added to `EVENT_TYPES` explicitly.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `handle-event.ts:51-67` / `LiveTaskProvider.test.ts`
  Evidence: The `AGENT_ACTIVITY_EVENT_NAMES` set is a data-driven guard that replaces the 15-arm inline `if` chain in the legacy `handleEvent`. The set is exported but has no direct test asserting that `routeEvent` calls `queryClient.invalidateQueries({ queryKey: ['agent-activity'] })` when an event name in the set is passed. The existing tests cover the domain handlers but the `routeEvent` orchestrator's agent-activity guard is tested only incidentally (no test sends a `coder_text_chunk` or `session.liveness` event through the mount).
  SuggestedAction: Add a test in `LiveTaskProvider.test.ts` that sends an agent-activity-class event (e.g. `'coder_text_chunk'`) through the mounted provider and asserts `invalidateQueries` was called with `{ queryKey: ['agent-activity'] }`.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `handle-event.ts` vs `run-lifecycle-toast.ts`
  Evidence: Two different type aliases represent the same shape: `QueryClientLike` in `handle-event.ts:21` and `QueryClient` in `run-lifecycle-toast.ts:5`, both defined as `ReturnType<typeof useQueryClient>`. Additionally, `run-lifecycle-toast.ts` imports `useQueryClient` as a runtime import solely for a type-level operation (`typeof useQueryClient`).
  SuggestedAction: Consolidate to a shared `QueryClientLike` type alias (or use TanStack's `QueryClient` directly where it's already available via `@tanstack/react-query`). Consider using `import type { QueryClient }` where only the type is needed.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `LiveTaskProvider.test.ts` — `StageFailed` + rebase test at line 778
  Evidence: The `StageFailed` + rebase payload test verifies `rebaseEvents` and `toast.error` but does not verify `['issues']` invalidation. The invalidation for rebase-conflict arms is tested in the `WorkflowRunFailed` counterpart (line 766), but the `StageFailed` handler independently applies the same outcome through a different code path (`stageHandler` → `applyReverseDnsOutcome`). A regression in the `stageHandler`-specific `['issues']` invalidation path would not be caught by the existing `StageFailed` test.
  SuggestedAction: Add an `invalidateSpy` assertion for `{ queryKey: ['issues'] }` in the `StageFailed` + rebase test to match the `WorkflowRunFailed` coverage pattern.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: `handle-event.ts:147-149` / `model/run-lifecycle-toast.ts:41-53`
  Evidence: `notifyRunLifecycleToast` takes an `issueId` (string) and resolves the issue number via `findIssueNumber` from the query cache. The `workflowRunHandler` casts `ctx.parsed as { issueId: string }` but does not guard against missing `issueId`. If a `WorkflowRunFailed` event fires without an `issueId` field in the payload, `evt.issueId` is `undefined`, and `findIssueNumber(queryClient, undefined)` silently returns `null`, suppressing the error toast entirely — even if `issueNumber` is present in the payload. `notifyApprovalRequestedToast` has a fallback (`evt.issueNumber ?? (evt.issueId ? findIssueNumber(...) : null)`) but `notifyRunLifecycleToast` does not.
  SuggestedAction: Either add a fallback to `issueNumber` in `notifyRunLifecycleToast` (matching `notifyApprovalRequestedToast`), or document the asymmetry.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: `handle-event.ts:153-154` — `approvalHandler`
  Evidence: `invalidateApprovalWait` is called with `ctx.queryClient as QueryClient` (a cast from `QueryClientLike` to TanStack's `QueryClient`). The cast is a no-op since both types resolve to the same concrete class, but it silently relies on the cast always being safe — if TanStack changes `useQueryClient`'s return type, the cast would mask the error.
  SuggestedAction: If `invalidateApprovalWait`'s signature is loosened to accept `QueryClientLike`, the cast can be removed. Otherwise, this is fine as-is (same pattern existed in the legacy `LiveTaskProvider.tsx`).
  Status: pre-existing

<promise>PASS</promise>
