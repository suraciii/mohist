# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test expectation
  Evidence: `buildWork` in `packages/runner/tests/executor-workspace-boundary.spec.ts` was changed to accept four arguments, but its only call still supplied the removed IssueId argument. Runner test typechecking therefore failed with TS2554.
  Verification: `npm run typecheck:tests -w packages/runner && npm run test:run -w packages/runner` passed (1,028 tests). The repaired snapshot also passed `npm test`.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: dead legacy contract cleanup
  Evidence: `IssueWorkflowProfileYamlResponse` and its fixtures still required `issueKey`, although `IssueWorkflowProfileResponse` no longer returns it. Removed the unused field and fixture values from `packages/web/src/entities/issue/model/issue.ts`, `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.test.tsx`, and `packages/web/src/pages/issue-detail/ui/_issueDetailMsw.tsx`.
  Verification: `npm run typecheck -w packages/web && npm run test:run -w packages/web -- src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.test.tsx src/pages/issue-detail/ui/IssueDetailPage.test.tsx` passed (50 tests).
  Status: resolved

## Blocking Items

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260716165000_MigrateEpicAffiliationToIssues.cs`
  Evidence: The affiliation migration updates an Issue only when the legacy relation query derives a non-null EpicNumber (lines 58-71). An Issue already carrying a stale `Issues.EpicNumber` or JSON `state.epicNumber`, but no `EpicActiveIssues` or `EpicIssues` row, retains that stale affiliation. The conflict guard also ignores this state (lines 52-55), and the final cutover preserves it with `COALESCE` in `20260716190000_RemoveLegacyIssueEpicIdentity.cs:100`. Once the membership tables are dropped, the stale value becomes permanent Issue-owned truth, incorrectly affecting Epic progress and link behavior.
  SuggestedAction: Make the migration derive the complete affiliation value from the legacy membership authority, explicitly clear missing affiliation from both the column and state JSON, and add a migration spec that seeds the stale-value/no-membership case from the previous schema.
  Verification: Migrate an Issue with `EpicNumber = 7` and `state.epicNumber = 7`, but no legacy membership rows, through `20260716190000_RemoveLegacyIssueEpicIdentity`; assert both persisted and deserialized affiliation are null.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: generic AgentSession launch context
  Evidence: The number-only contract is bypassed for generic AgentSession metadata. `AgentSessionLaunchContextRef.EpicNumber` is a `string` in `packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:115`, propagated unchanged through `AgentLaunchContext` and `GenericAgentSessionContext` (`packages/server/src/Mohist.Server/Agent/Services/IAgentLauncher.cs:85`, `packages/server/src/Mohist.Server/Sessions/Services/GenericAgentSessionMetadata.cs:20`) and persisted as a label (line 101). The Web sends the same string shape (`packages/web/src/entities/agent/api/agent-sessions.ts:10` and `packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:238`). The route spec explicitly accepts and persists `epic-7` (`AgentSessionLaunchRoutesSpecs.cs:160`). This leaves current session-origin metadata with an opaque Epic identity after all other Issue/Epic references were migrated to project-scoped numbers.
  SuggestedAction: Use nullable positive integer EpicNumber across the request, Web DTO, launch context, and metadata. Reject malformed/non-positive values and add round-trip and invalid-input coverage, including Project-scoped lookup where the context must refer to an existing Epic.
  Verification: Launch with numeric Epic 7 and verify canonical metadata; reject `epic-7`, zero, negative, and cross-project references.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionLineage.cs`
  Evidence: Generic sessions persist Issue/Epic context in their own labels (`GenericAgentSessionMetadata.cs:99-102`), but `AgentSessionLineage.BuildExtensions` emits those extensions only when `source-kind == workflow` (lines 51-69). For `agent-launch`, it emits only `agentid` (lines 70-76), discarding its locally committed Issue/Epic context. This violates the accepted AgentSession producer matrix, which requires optional local `issue` and `epic` context. `AgentSessionLineageTests.cs:55-62` currently asserts the incorrect omission instead of exercising the real append path with generic context.
  SuggestedAction: Stamp non-empty Issue/Epic labels for every AgentSession producer; keep workflow-run and stage conditional on workflow-origin metadata. Replace the omission assertion with real store-append conformance coverage for generic Issue/Epic context.
  Verification: Append a generic AgentSession event with Issue 42 and Epic 7 context and assert `projectid`, `sessionid`, `agentid`, `issue=42`, and `epic=7`; verify absent optional context is omitted.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: Web CloudEvent routing normalization
  Evidence: `mergeIssueLineage` in `packages/web/src/app/providers/model/event-envelope.ts:48` copies only `extensions.issue`, and only when the payload does not already carry `issueNumber` (lines 52-63); it never copies `extensions.projectid`. AgentSession event payloads have neither field (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSessionEvent.cs:20`). As a result, the project-aware handlers in `packages/web/src/widgets/coder-session/model/useSessionTimeline.ts:221` reject valid context-compacted and context-health-updated events at lines 412-422. On payload/envelope disagreement, non-timeline routing still retains the payload Issue rather than canonical envelope context. Existing timeline tests cover only `issue` and not `projectid` or this live-session path.
  SuggestedAction: Build a separate routing projection from canonical envelope extensions (`projectid`, `issue`, and applicable context), preferring it over payload without mutating the original payload used for display. Add envelope-disagreement and AgentSession live-event tests.
  Verification: Deliver a real `com.mohist.agent-session.context-health-updated` envelope with `extensions: { projectid, issue }` and payload-only health fields; assert the current Issue session reducer updates. Also assert a conflicting payload Issue cannot change routing.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: Web realtime subscription and membership cache invalidation
  Evidence: The server publishes `com.mohist.issue.epic-changed` (`packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:166`), but the Web `REVERSE_DNS_EVENT_TYPES` and `EVENT_TYPES` omit it (`packages/web/src/shared/lib/canonical-event-types.ts:1-86`). `events-hub.ts:62-65` subscribes only to `EVENT_TYPES`, and the route table has no handler to invalidate Issue and Epic query keys. A link, unlink, or cross-Epic move in another browser therefore leaves the current client's Epic membership and Issue affiliation stale until an unrelated refetch.
  SuggestedAction: Add the canonical type to the subscription set and route it to a handler that invalidates the project Issue list/detail and affected Epic lists/details. Add a multi-client realtime invalidation test.
  Verification: With a mounted client for a Project, deliver `IssueEpicChanged` after a link/move and assert its `['issues']` and `['epics']` queries invalidate and refetch.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

(none)

<promise>FAIL</promise>
