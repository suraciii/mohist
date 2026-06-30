# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Inbox/InboxSubscriptionStoreSpecs.cs, packages/server/tests/Mohist.Server.Tests/Support/InboxProjectionTestSupport.cs, packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs
  Evidence: The post-build candidate does not pass the required server verification. `InboxSubscriptions.ProjectId` is now a required FK to `Projects.Id` (`MohistDbContext.cs:576-589`; migration lines `20260629003200_AddInboxSubscriptionsTable.cs:21-41`), but the new store and projection tests call `InboxSubscriptionStore.SetAsync("proj_a", ...)` without creating a matching `ProjectRow` (`InboxSubscriptionStoreSpecs.cs:36,59,81,110,128,150`; `InboxProjectionTestSupport.cs:115-129`). `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~InboxSubscription|FullyQualifiedName~InboxProjectionHandler"` failed with 13 failures and 51 passes; failures are `SQLite Error 19: 'FOREIGN KEY constraint failed'` from `InboxSubscriptionStore.SetAsync` at line 83. `npm test` also emitted the same failures before the 120s tool timeout. [disallowed:test repair is local, but unresolved behavior findings remain and the full candidate requires owner repair]
  SuggestedAction: Seed valid `ProjectRow` records in the subscription store/projection test fixtures before writing subscription rows, preferably through a shared helper so all project-scoped persistence tests exercise the FK contract consistently. Re-run the focused filter and full `npm test`.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~InboxSubscription|FullyQualifiedName~InboxProjectionHandler"` should pass with no FK failures; `npm test` should complete successfully.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs
  Evidence: Disabling a kind returns before `InboxStore.InsertAsync` (`InboxProjectionHandler.cs:140-156`), so no durable idempotency row or skipped-event marker is written for the observed source event. The existing idempotency only dedupes inserted `InboxItems` by source/event id (`MohistDbContext.cs:567-570`). If the same CloudEvent is replayed after the kind is re-enabled, the subscription gate now passes and the old event is inserted, violating the issue/spec rule that re-enabling does not recreate items for events observed while disabled (`project-inbox-subscription/spec.md:26-30`). The current re-enable test uses a different event id after re-enable (`InboxProjectionHandlerSpecs.cs:866-888`), and the replay test only replays while the kind remains disabled (`InboxProjectionHandlerSpecs.cs:912-923`), so this case is untested. [disallowed:product behavior/data model change]
  SuggestedAction: Make skipped source events durable enough to remain skipped across later replays, or otherwise compare replayed event time against subscription history. Add a regression test: disable a kind, handle event id X, re-enable the kind, replay event id X, assert no inbox item is created.
  Verification: Add the regression test above and run the focused projection suite plus `npm test`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/InboxRoutes.cs, openspec/changes/issue-285/specs/project-inbox-subscription/spec.md
  Evidence: The delta spec says the update operation accepts desired state "for one or more of the four kinds" (`spec.md:78-83`), but the implemented `PUT /subscription` rejects any request missing one of the four keys (`InboxRoutes.cs:108-118`) and API tests assert missing-key 400 (`InboxSubscriptionApiSpecs.cs:103-126`). The design and tasks intentionally chose whole-object PUT, so the candidate has a spec/API contract mismatch rather than a simple implementation typo. [disallowed:public contract/spec-sync change]
  SuggestedAction: Resolve the contract explicitly: either implement partial update semantics for one-or-more keys, or update the spec to require whole-object replacement and keep missing-key rejection as intended.
  Verification: Add/update API tests to match the chosen contract and run `npm test`.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Inbox/InboxSubscriptionStore.cs, packages/server/src/Mohist.Server/Api/InboxRoutes.cs
  Evidence: First writes use a read-then-insert flow (`InboxSubscriptionStore.cs:55-83`). With `ProjectId` as the primary key, two concurrent first updates for the same project can both observe no row; one succeeds and the other throws a PK/unique conflict from `SaveChangesAsync`. The route lets that exception escape (`InboxRoutes.cs:138-139`), producing a 500 for an otherwise valid preference update. [disallowed:product behavior/error-handling change]
  SuggestedAction: Use an atomic SQLite upsert, or catch the insert conflict and retry as an update/reload. Add a concurrent first-write store or API test.
  Verification: Run the new race test repeatedly plus `npm test`.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: packages/web/src/pages/settings/ui/InboxSubscriptionSection.tsx
  Evidence: Load failures are rendered as editable all-enabled preferences. The component reads only `data` and `isLoading` from `useInboxSubscription()` (`InboxSubscriptionSection.tsx:35-38`), initializes local state to all-enabled (`InboxSubscriptionSection.tsx:28-39`), and renders editable switches whenever `isLoading` is false (`InboxSubscriptionSection.tsx:64-85`). If `GET /inbox/subscription` fails, the UI looks like persisted all-enabled state; the next toggle sends a full-object PUT from that fallback state and can overwrite real stored preferences. [disallowed:product behavior/data safety]
  SuggestedAction: Handle `isError`/`error` from the query with an inline error/retry state and disable or hide switches until server state is loaded. Let the server's successful all-enabled response be the only source of "no stored preferences" defaulting.
  Verification: Add a component test with `{ data: undefined, isLoading: false, isError: true }` asserting no editable switches and no mutation path, then run `npm run test:run -w packages/web`.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: packages/web/src/pages/settings/ui/InboxSubscriptionSection.tsx, packages/web/src/entities/inbox/api/queries.ts
  Evidence: Toggle changes are optimistic local full-object PUTs (`InboxSubscriptionSection.tsx:48-55`), while switches remain enabled during pending mutations (`InboxSubscriptionSection.tsx:73-80`). `useUpdateInboxSubscription` only invalidates on success and does not serialize writes or reconcile mutation order (`queries.ts:95-110`). Starting all enabled, turning off `workflow_failed` sends payload A; immediately turning off `approval_requested` sends payload B with both disabled. If payload B commits first and payload A commits last, the server ends at `workflow_failed=false, approval_requested=true`, losing the user's later change. [disallowed:product behavior change]
  SuggestedAction: Serialize/coalesce updates, disable all switches while an update is pending, or track mutation order with optimistic cache reconciliation so stale responses cannot overwrite newer user intent.
  Verification: Add a deferred-mutation test that resolves the second request before the first and asserts the final persisted/cache state matches the latest draft; run `npm run test:run -w packages/web`.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: packages/web/src/pages/settings/ui/InboxSubscriptionSection.tsx, packages/web/src/entities/inbox/api/queries.ts
  Evidence: A failed PUT leaves the unsaved optimistic draft active. The component updates `draftRef` and `draft` before save completion (`InboxSubscriptionSection.tsx:48-55`), but mutation errors only toast in the hook (`queries.ts:107-109`) and do not rollback/refetch. A later successful toggle uses `draftRef.current`, so it can persist a prior failed change unintentionally. [disallowed:product behavior change]
  SuggestedAction: Roll back to the last confirmed subscription on error, refetch on failure, or update confirmed state from mutation success only while showing a visible save error.
  Verification: Add a component test where a mutation fails and assert the switch returns to the last confirmed state and the next payload does not include the failed change.
  Status: open

- [ID: item-8]
  Severity: cleanup
  Scope: packages/web/src/entities/inbox/model/types.ts
  Evidence: `InboxSubscription` manually repeats the four notification-kind keys (`types.ts:31-38`) instead of deriving from `NotificationKind`, so the "one toggle per notification kind" invariant can drift if `NOTIFICATION_KINDS` changes. `InboxSubscriptionApiData` and `InboxSubscriptionResponse` are declared but unused (`types.ts:40-47`), while the shared `request<T>` wrapper already unwraps response data. This is small, but it is new code in the changed surface. [disallowed:cleanup is local, but not repaired because candidate already fails on unresolved behavior/test issues]
  SuggestedAction: Replace the interface with `export type InboxSubscription = Record<NotificationKind, boolean>` and remove unused response wrapper types unless they are consumed.
  Verification: Run `npm run typecheck -w packages/web`.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

_None._

## Verification

- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~InboxSubscription|FullyQualifiedName~InboxProjectionHandler"` failed: 13 failed, 51 passed. Failures are FK constraint errors from subscription test setup.
- `npm test` did not complete within the 120s tool timeout, but emitted the same FK constraint failures before timeout.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 199 test files passed; 3010 tests passed, 1 skipped.

<promise>FAIL</promise>
