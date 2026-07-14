# Review Report

## Result: FAIL

## Repaired Items

None. The findings require architectural, behavioral, or public-contract decisions and were not repaired under the review policy.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Project/Services/ProjectEventQuerier.cs`
  Evidence: `ProjectEventQuerier` is in the `Project` domain but imports and invokes `Sessions.Services.TranscriptPartLoader` at lines 8 and 160. This violates the enforced one-way domain dependency rule and fails `Mohist.Server.ArchTests.ArchitectureRules.DomainModules_ShouldNotDependOnEachOther` at `packages/server/tests/Mohist.Server.ArchTests/ArchitectureRules.cs:344`. The domain design assigns cross-domain read assembly to AgentOps, not Project (`design/domain-analysis.md:36-38`). [disallowed:architectural judgment]
  SuggestedAction: Move the cross-aggregate event projection to AgentOps, or introduce a Session-owned read contract that avoids a `Project -> Sessions` dependency. Do not weaken the architecture test.
  Verification: `dotnet test packages/server/tests/Mohist.Server.ArchTests/Mohist.Server.ArchTests.csproj --no-restore`
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Project/Services/ProjectEventQuerier.cs`
  Evidence: Each persisted event source is fully materialized before ordering and `Take(limit)` at lines 97-105, 116-127, and 137-148. The query also loads every project session at lines 68-70, synthesizes an opened row for every one at lines 151-153, and reads all matching session-close transcript facts at lines 155-164. `useProjectEvents` polls this endpoint every five seconds (`packages/web/src/entities/project/api/queries.ts:104-112`), so a request with `limit=200` still scans and holds the entire project history in memory.
  SuggestedAction: Define a queryable, shared ordering key and retrieve bounded candidates before materialization. Limit lifecycle projection to sessions that can enter the requested window, and add a large-history query-bound spec.
  Verification: Seed a project with substantially more than the requested limit across all source tables; assert bounded rows/materialization and stable response latency for `GET /api/projects/{project}/events?limit=200`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: project event API pagination and Activity filtering
  Evidence: The API caps output at 1,000 (`ProjectEventsRoutes.cs:17-40`) and exposes no continuation. The Activity query requests only 200 rows (`packages/web/src/entities/project/api/queries.ts:104-112`), then applies the attention/type filters only on that local window (`packages/web/src/pages/activity/ui/ActivityPage.tsx:173-182`). A project with 200 newer routine events and an older failure therefore renders no result for "Attention only", contrary to the evidence-finding requirement. [disallowed:public contract change]
  SuggestedAction: Add an opaque continuation cursor and paginate, or implement server-side attention/type filtering that guarantees matching evidence can be found.
  Verification: Add an API/UI spec with more than 200 newer routine events and an older failure; selecting "Attention only" must show that failure.
  Status: open

- [ID: item-4]
  Severity: minor
  Scope: `packages/server/src/Mohist.Server/Project/Services/ProjectEventQuerier.cs`
  Evidence: Each bucket is capped using only `Time, Id` (lines 101-104, 121-123, and 141-144), but the final ordering also uses origin, source, type, and envelope ID (lines 46-54). IDs are source-local and same-time events from different aggregates are valid, so a row can be discarded before the advertised final order selects it. Synthetic opened-row IDs additionally depend on the unsorted session query at lines 68-70 and 151-153. This violates the deterministic ordering required for the Activity projection.
  SuggestedAction: Use one complete ordering tuple for all bounded candidate selections, synthetic inputs, final merge, and any continuation token.
  Verification: Add a limit-one spec with same-time, same-ID events from different source aggregates and assert the same winner across repeated executions.
  Status: open

- [ID: item-5]
  Severity: blocking
  Scope: `packages/web/src/pages/session/data/useIssueSessionDataSource.tsx`
  Evidence: An Activity session link contains the stable session ID. The legacy route resolves that ID through `useCoderSessions`, but treats a fresh cached list as resolved even when it does not contain the newly created session (`sessionsResolved` at lines 161-163). It then calls the name-keyed metadata endpoint with the raw stable ID at lines 155 and 183-189. `useCoderSessions` keeps its list fresh for 30 seconds (`packages/web/src/entities/coder-session/model/useCoderSessions.ts:15-20`), so opening a new Activity entry during that interval permanently requests an invalid session name and ends in "Session not found".
  SuggestedAction: Resolve Activity targets by canonical stable session ID at the detail API boundary, or force a list refresh and defer the request until the canonical name is present.
  Verification: Cache an issue's session list, add a new session, open its Activity URL before the 30-second stale time expires, and assert metadata/transcript requests use a valid identifier and render the session.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/web/src/pages/session/data/useIssueSessionDataSource.tsx`
  Evidence: The page recognizes `?from=activity` and returns to Activity at lines 293-300, but lineage links rebuild only `?rt=` at lines 265-269 and sibling navigation omits the origin parameter at lines 362-384 and 434-440. Opening a sibling or lineage entry from an Activity-origin session silently changes its Back destination to the issue, losing the requested orientation.
  SuggestedAction: Carry the validated `from=activity` context when deriving sibling and lineage targets, with coverage for both routes.
  Verification: Open a session from Activity, navigate to a sibling and a lineage runtime entry, and assert each destination's Back link still targets the project-scoped Activity page.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: Activity navigation for generic agent sessions with issue context
  Evidence: `buildAgentSessionEventEntry` marks an `agent-launch` event generic at `packages/web/src/widgets/coder-session/model/activity-events.ts:288-301`, but still records its issue target at line 303. `ActivityEventEntry` renders workflow, session, agent, and runner chips only (`packages/web/src/pages/activity/ui/ActivityEventEntry.tsx:69-124`); it never renders `targets.issue`. A generic session associated with an issue thus has no visible direct issue link, even though the event carries that context.
  SuggestedAction: Render an issue target chip whenever it is available and is not already the primary destination.
  Verification: Add an Activity spec for an `agent-launch` event with `issueNumber`; assert both its generic session link and its project-scoped issue link are visible.
  Status: open

- [ID: item-8]
  Severity: warning
  Scope: Activity evidence failure and retry handling
  Evidence: `useActivityEvents` collapses recorded-event, agent-activity, and runner failures into one boolean and discards their refetch functions (`activity-events.ts:523-541`). `ActivityPage` then reports that the recorded event feed is incomplete at lines 244-248 even if only the runner request failed, and provides no retry action or live error announcement. This makes a diagnostic screen less actionable precisely when one of its sources is unavailable.
  SuggestedAction: Preserve source-specific error/refetch state, label the failed source accurately, expose an immediate retry control, and announce the failure with an alert role.
  Verification: Independently fail each of the three requests and assert the message identifies that source and its retry control restores the feed.
  Status: open

- [ID: item-9]
  Severity: minor
  Scope: `packages/web/src/entities/project/api/projectEvents.ts`
  Evidence: The exported wire type narrows `data` to `Record<string, unknown> | null` at line 15, while the server returns arbitrary `JsonElement` at `packages/server/src/Mohist.Server/Api/ProjectEventsRoutes.cs:58-99`. Valid CloudEvent payloads can be strings, numbers, booleans, or arrays, so TypeScript consumers receive a contract that excludes valid server responses.
  SuggestedAction: Model `data` as a JSON value or `unknown`, then use an explicit plain-object guard before key iteration in the Activity projection.
  Verification: Add server/client contract cases for scalar and array payloads, and verify Activity classification safely ignores unsupported payload shapes.
  Status: open

- [ID: item-10]
  Severity: test-gap
  Scope: `packages/web/tests/browser/`
  Evidence: The changed Activity screen contains responsive filters, collapsible attention/routine zones, and new project-scoped navigation, but `packages/web/tests/browser/` has no Activity scenario. The new Activity coverage is Vitest-only (`packages/web/tests/ActivityEvidenceView.spec.tsx:71-315`), which cannot validate real-layout wrapping, overflow, or navigation at mobile and desktop viewports.
  SuggestedAction: Add browser coverage for Activity at desktop and narrow mobile widths, including attention/routine rendering, filter wrapping, no horizontal overflow, and issue/session/runner navigation.
  Verification: `npm run test:browser -w packages/web`
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: warning
  Scope: unrelated Web browser suite stability
  Evidence: `npm run test:browser -w packages/web` produced 36 passing and 3 failing tests in unchanged `epic-detail-mobile-overflow.spec.ts`, `epic-dialog-mobile-overflow.spec.ts`, and `settings-search.spec.ts`. The failures are missing expected page elements under parallel execution, and the prior candidate run completed the same browser suite successfully. None of those files are in the issue-402 candidate diff.
  SuggestedAction: Stabilize the shared browser fixture and test isolation in a separate change; keep issue-402 browser coverage independent of this existing suite flakiness.
  Status: pre-existing

<promise>FAIL</promise>
