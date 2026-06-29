# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/app/providers/LiveTaskProvider.tsx`
  Evidence: High-attention inbox hints now show notices in the `InboxItemPersisted` branch at `LiveTaskProvider.tsx:531-557`, but the older source-event notice paths remain active for the same workflow facts: workflow failures call `notifyRunLifecycleToast(..., 'error')` at `LiveTaskProvider.tsx:492-516`, and approval requests call `notifyApprovalRequestedToast` at `LiveTaskProvider.tsx:518-523`. The server emits the inbox hint from the projection after handling those same source events (`InboxProjectionHandler.cs:149-179`), so a normal persisted `workflow_failed` or `approval_requested` item can produce a source-event toast and then an inbox-hint toast. This violates the issue's requirement that the browser receive "a new inbox item exists" instead of reinterpreting raw workflow events for inbox notification decisions, and it violates duplicate-notice suppression. It also leaks through on the inbox page: the source-event approval toast only checks the viewed issue number (`LiveTaskProvider.tsx:292-300`) and does not suppress `/inbox`, while the new hint path does (`inbox-effects.ts:125-132`). [disallowed:behavior-change]
  SuggestedAction: Make high-attention inbox notices originate from the durable inbox hint path only, or otherwise dedupe/suppress the legacy source-event toasts whenever they correspond to inbox-projected events. Add a regression test that dispatches the source event followed by `com.mohist.inbox.item-persisted` and asserts only one notice, including the `/inbox` route suppression case.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed, but no current test covers the source-event plus inbox-hint sequence.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/UserNotificationDispatcher.cs`
  Evidence: The dispatcher only applies the project gate when the event has `extensions["projectid"]` and the connection has a non-empty project affinity (`UserNotificationDispatcher.cs:260-271`). A subscribed connection with no project affinity therefore receives project-stamped inbox hints for every project. This behavior is explicitly asserted in `ProjectIsolationIntegrationSpecs.cs:131-163` and `UserNotificationDispatcherProjectFilterSpecs.cs:139-178`. That is weaker than the issue/spec wording that inbox hint delivery is project-scoped and delivered only to Web sessions subscribed to the owning project, with no cross-project leakage. The payload is identity-only, but it still contains `itemId`, `projectId`, `kind`, `issueId`, and `issueNumber`, so this is still cross-project inbox metadata exposure for any subscribed no-affinity connection. [disallowed:public-routing/security-posture]
  SuggestedAction: For `com.mohist.inbox.item-persisted` events, require a declared connection project that exactly matches the event `projectid`. If cross-project/admin consumers are needed, define an explicit contract for them rather than inheriting type-only matching for inbox hints.
  Verification: Changed server specs passed with `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~InboxProjectionHandlerRealtimeHintSpecs|FullyQualifiedName~ProjectIsolationIntegrationSpecs|FullyQualifiedName~UserNotificationDispatcherProjectFilterSpecs|FullyQualifiedName~ConnectionSubscriptionRegistryProjectIdSpecs|FullyQualifiedName~MohistHubProjectAffinitySpecs|FullyQualifiedName~InboxProjectionHandlerSpecs"`; the current tests confirm the fallback behavior rather than strict inbox isolation.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/web/src/pages/inbox/ui/InboxPage.test.tsx`, `packages/web/src/widgets/app-shell/ui/AppSidebar.test.tsx`, `packages/web/src/widgets/app-shell/ui/MobileBottomNav.test.tsx`
  Evidence: The tests cover the pieces separately, but not the acceptance-level realtime flow for the inbox list or unread count. `LiveTaskProvider.test.ts:263-285` asserts that a hint invalidates `['inbox', projectId]`; `InboxPage.test.ts:396-413` only asserts the page renders whatever `useInbox` returns; sidebar/mobile badge tests mutate cached data directly (`AppSidebar.test.tsx:241-275`, `MobileBottomNav.test.tsx:130-188`). There is no integration-style test that dispatches an inbox hint, observes the inbox API/query refetch, and verifies the list/count update from the authoritative API result. This misses the acceptance criteria for inbox list refresh and unread count invalidation end-to-end.
  SuggestedAction: Add a web test with a real `QueryClient` and mocked inbox API data that mounts the shell or inbox page, dispatches `com.mohist.inbox.item-persisted`, and waits for the refetched API result to update the list and unread badge without navigation.
  Verification: `npm run test:run -w packages/web` passed, but the described acceptance-level flow is not currently exercised.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: full server test suite
  Evidence: `npm test` failed in `Mohist.Server.Tests.Specs.SystemSpecs.UpdateSpecs.UpdateServer_WhenReadinessDoesNotBecomeReady_ReturnsFailure` because the expected readiness error substring was absent from the returned message. This failure is in the update/readiness test area and not in the changed inbox realtime files.
  SuggestedAction: Investigate separately or re-run after syncing the current update-spec expectations.
  Status: pre-existing

<promise>FAIL</promise>
