# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs, packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260629003200_AddInboxSubscriptionsTable.cs, packages/server/tests/Mohist.Server.Tests/Specs/Inbox/InboxSubscriptionsMigrationSpecs.cs
  Evidence: The issue and task require `InboxSubscription` to be project-scoped and T-001 explicitly requires `InboxSubscriptions` to have `PK ProjectId, FK to Projects(Id)`. The EF model config for `InboxSubscriptionRow` only declares the table, primary key, columns, and required flags, with no relationship to `Projects` (`MohistDbContext.cs:576-586`). The migration creates only `PK_InboxSubscriptions` and no `table.ForeignKey(...)` constraint (`20260629003200_AddInboxSubscriptionsTable.cs:21-35`). The migration tests assert columns and primary key but never assert the project foreign key (`InboxSubscriptionsMigrationSpecs.cs:17-47`), so orphan subscription rows are not guarded at the schema boundary. This weakens the project-scoped configuration contract and leaves stale/orphan preference state possible if a project is deleted or an invalid project id reaches the store. [disallowed:public contract/data-safety schema change]
  SuggestedAction: Add an EF relationship from `InboxSubscriptionRow.ProjectId` to `ProjectRow.Id`, regenerate the migration and model snapshot so `InboxSubscriptions.ProjectId` has an FK to `Projects(Id)`, and add a migration/model test that verifies the FK exists.
  Verification: Run `npm test` and ensure the new FK regression test fails before the fix and passes after it.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/InboxRoutes.cs
  Evidence: `PUT /api/projects/{projectRef}/inbox/subscription` binds a raw `JsonElement` and immediately calls `body.EnumerateObject()` (`InboxRoutes.cs:84-90`). `EnumerateObject()` throws if the JSON body is not an object, and `JsonSerializer.Deserialize<InboxSubscriptionDto>(...)` can throw for wrong value types such as strings, numbers, arrays, or `null` where bools are expected (`InboxRoutes.cs:105-107`). Those malformed public API inputs are not caught and converted to `ApiResults.BadRequest`, even though nearby validation handles unknown and missing keys as 400s. This creates a 500-class error path for invalid client input instead of a controlled contract rejection. [disallowed:public contract/error-handling behavior change]
  SuggestedAction: Validate `body.ValueKind == JsonValueKind.Object` before enumerating, validate each required property value is `True` or `False`, and catch `JsonException`/`InvalidOperationException` around DTO parsing to return `ApiResults.BadRequest(...)` without persisting.
  Verification: Add API tests for array body, scalar body, and non-boolean property values; run `npm test`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/web/src/pages/settings/ui/InboxSubscriptionSection.tsx
  Evidence: Each toggle mutation is built from the last query snapshot (`...subscription`) and the changed kind (`InboxSubscriptionSection.tsx:30-35`). The controls stay enabled during mutation and no local draft/optimistic cache update is applied (`InboxSubscriptionSection.tsx:49-56`). Because the API uses whole-object `PUT`, two quick toggle changes before the subscription query refetches can send the second request from stale state and overwrite the first change. Example: starting from all enabled, turning off `workflow_failed` sends `{ workflow_failed: false, ... }`; immediately turning off `approval_requested` can send `{ workflow_failed: true, approval_requested: false, ... }`, re-enabling workflow failures unintentionally. This violates the UI requirement that changes persist reliably through the subscription API. [disallowed:product behavior change]
  SuggestedAction: Maintain a local draft state synchronized from the query, optimistically update the query cache with the mutation variables, or disable all toggles while the update mutation is pending so stale whole-object writes cannot race each other.
  Verification: Add a UI test that performs two rapid toggle changes before refetch and asserts the second mutation preserves the first change; run `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: packages/web/src/pages/settings/ui/InboxSubscriptionSection.tsx
  Evidence: The settings surface includes internal-ish copy such as `Inbox Notifications`, `Notification Kinds`, and "Choose which notification kinds are recorded in this project's inbox" (`InboxSubscriptionSection.tsx:39-43`). The four toggle labels themselves are product-facing and no raw kind strings are shown, so this is not a functional spec failure, but the issue asked the UI to describe the outcome in product language rather than event names.
  SuggestedAction: Consider replacing the heading/body copy with outcome-oriented language such as "Inbox recording" and "Choose which workflow updates create future inbox items."
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None._

## Verification

- `npm test` passed. Relevant result from the run: 54 runner test files passed, 2 skipped; 786 tests passed, 23 skipped. The output was truncated by the tool, but the command completed successfully.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 199 test files passed; 3009 tests passed, 1 skipped.

<promise>FAIL</promise>
