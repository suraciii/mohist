# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `openspec/changes/issue-286/tasks.json`, `openspec/changes/issue-286/design.md`, `packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs`, `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260629003151_AddInboxItemsTable.cs`
  Evidence: The implementation deduplicates by the CloudEvents identity pair `(SourceEventSource, SourceEventId)` (`MohistDbContext.cs` creates `UQ_InboxItems_SourceEvent` over both columns; the migration creates the same unique index). That is a defensible interpretation of CloudEvents source-event identity and is covered by `InboxStoreSpecs.InsertAsync_SameSourceEventIdAcrossDifferentSources_CreatesDistinctItems`, but some workflow artifact text still says `UNIQUE(SourceEventId)` / idempotent by `SourceEventId` alone. This does not break the product candidate, but it can mislead future maintainers reading the artifacts as implementation guidance.
  SuggestedAction: If these artifacts are carried forward, align the wording to "CloudEvent source plus id" so the spec/design/tasks match the shipped schema.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/entities/inbox/model/useInboxLiveRefresh.ts`, `packages/server/src/Mohist.Server/Events/Hub/EventBridge.cs`, `packages/server/src/Mohist.Server/Infrastructure/Events/UserNotificationDispatcher.cs`
  Evidence: The inbox page invalidates its query for any of the four inbox-relevant event types received on the SignalR connection. The server hub/dispatcher currently filters dynamic clients by event type only, not by event project, so an inbox-relevant event from project B can cause a harmless refetch of project A's inbox. The API remains project-scoped and no cross-project data is exposed, so this is not a correctness or security blocker.
  SuggestedAction: When live event routing grows, consider project-aware SignalR dispatch or client-side payload project filtering to reduce unnecessary refetches.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/entities/inbox/model/types.ts`
  Evidence: `parseNotificationKind` silently maps unknown values to `workflow_failed`. It is currently only covered by tests and not used in the inbox client/page path, so it does not affect the reviewed behavior. If reused later for server response normalization, it could make bad data look like a real workflow failure.
  SuggestedAction: Prefer rejecting or surfacing unknown notification kinds at the boundary if this parser becomes production input handling.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: workflow projection reliability
  Evidence: The in-process event bus and projection handler both log/swallow handler failures. A transient projection/store failure after the source event is appended can therefore miss an inbox item until a future replay/backfill exists. This risk is explicitly documented in `openspec/changes/issue-286/design.md`; the current issue did not add a replay worker.
  SuggestedAction: Add a replay/backfill path before relying on the inbox as an audit-grade notification log.
  Status: out-of-scope

## Acceptance Criteria Evidence

- Project inbox route/page exists at `/:projectName/inbox` in `packages/web/src/app/App.tsx`, with desktop/mobile navigation entries in `AppSidebar.tsx` and `MobileBottomNav.tsx`.
- The UI lists kind, issue number, title, relative creation time, unread/read state, issue links, empty/loading/error states, and read/archive actions in `packages/web/src/pages/inbox/ui/InboxPage.tsx`; coverage is in `InboxPage.test.tsx` and `App.test.tsx`.
- The API exposes list, mark-one-read, mark-all-read, and archive under `/api/projects/{projectRef}/inbox` in `packages/server/src/Mohist.Server/Api/InboxRoutes.cs`; integration coverage is in `InboxApiSpecs.cs`.
- Durable project-scoped storage is implemented by `InboxItemRow`, `InboxStore`, `InboxQuerier`, `MohistDbContext` mapping, and migration `20260629003151_AddInboxItemsTable`.
- Server-side projection maps exactly the four MVP events to `workflow_failed`, `approval_requested`, `issue_started`, and `issue_completed` in `InboxProjectionHandler.cs`; projection coverage is in `InboxProjectionHandlerSpecs.cs`.
- Idempotency, project isolation, read state, archive/dismiss behavior, list ordering, migration shape, and UI path are covered by the added server and web tests.

## Verification

- `mo issue show 286 --project-id proj_f6c141d63b6243bfbb481737b2243b87`
- `git diff --name-status master...HEAD`
- `git diff --check master...HEAD`
- `npm test`
- `npm run typecheck -w packages/web`
- `npm run test:run -w packages/web`

<promise>PASS</promise>
