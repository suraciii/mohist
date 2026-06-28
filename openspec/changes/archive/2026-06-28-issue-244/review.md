# Review Report

## Result: FAIL

## Repaired Items

- (none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs; packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionTranscriptStore.cs
  Evidence: Workflow session list/detail status fields can report the wrong terminal close event for multi-turn/recovered sessions. The transcript store assigns `AgentSessionTranscriptPartRow.Sequence` within a single turn by querying only the current `TurnId` (`AgentSessionTranscriptStore.cs:108-131`), while turn ordering is stored separately on `AgentSessionTranscriptTurnRow.Sequence` (`AgentSessionTranscriptTurnRow.cs:5-10`). The new terminal projection mixes close parts from every turn and orders them only by part sequence/id (`AgentSessionQuerier.cs:672-676`, `AgentSessionQuerier.cs:812-817`). If turn 1 has a close part at sequence 6 and a later recovered/follow-up turn has a close part at sequence 2, `LastOrDefault` selects the older close. That makes `Status`, `CompletedAt`, `FailureReason`, and `ExitCode` stale in `/api/workflow-runs/{id}/sessions` and `/api/workflow-runs/{id}/sessions/{name}`, breaking status filtering, duration sorting, and retry/recovery discovery. [disallowed:reason] Repair would change public API behavior and recovery semantics, not a small local review fix.
  SuggestedAction: Select terminal facts in chronological transcript order by owning turn sequence, then part sequence/id, or by another persisted session-global event ordering if available. Add a regression test with two turns where the earlier close has a higher part sequence than the later close, and assert both list and detail APIs expose the later terminal status/failure/completion data.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~WorkflowSessionSpecs -p:SkipWebBuild=true` passed 2 tests, but current tests only cover single-turn terminal facts and do not exercise this ordering case.
  Status: unresolved

- [ID: item-2]
  Severity: test-gap
  Scope: packages/web/src/entities/coder-session/model/useWorkflowRunSessions.ts; packages/web/src/entities/coder-session/model/useWorkflowRunSessions.test.tsx
  Evidence: `useWorkflowRunSessions` changed its state model and gates live-event updates by `workflowRunId` (`useWorkflowRunSessions.ts:14-122`), but the new test only covers clearing stale sessions while switching workflow runs (`useWorkflowRunSessions.test.tsx:57-88`). There is no coverage for the modified `coder_session_completed`, `coder_session_status_changed`, or `usage.updated` paths (`useWorkflowRunSessions.ts:55-113`). A regression in live status, completion timestamp, failure reason, ACP id, or usage updates would pass the current suite and directly affect the new panel/sidebar data.
  SuggestedAction: Capture the mocked `onAgentEvent` callbacks in `useWorkflowRunSessions.test.tsx` and assert completed, status-changed, and usage events update matching sessions, ignore nonmatching sessions, and do not update after a workflow-run switch.
  Verification: `npm run test:run -w packages/web -- WorkflowSessionsPanel useWorkflowSessionFiltering useSiblingSessions useWorkflowRunSessions SessionPage` passed 221 tests; `npm run test:run -w packages/web` passed 2719 tests with 1 skipped. These passing runs do not cover the changed live-event branches above.
  Status: unresolved

- [ID: item-3]
  Severity: cleanup
  Scope: packages/web/src/widgets/issue-workflow/model/useSiblingSessions.test.tsx
  Evidence: The test name at `useSiblingSessions.test.tsx:218` says null `workflowRunId` is handled "without invoking the data hook", but the assertion at `useSiblingSessions.test.tsx:225` correctly expects `useWorkflowRunSessions(null)` to be called. The implementation must call hooks unconditionally, so the title documents the opposite of the intended behavior.
  SuggestedAction: Rename the test to state that null workflow ids return an empty sibling set while passing null through to the data hook.
  Verification: `npm run test:run -w packages/web -- useSiblingSessions` is covered by the targeted web run above.
  Status: unresolved

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: openspec/changes/issue-244/design.md
  Evidence: The design correctly notes the workflow session API/read model extension in constraints (`design.md:20-24`), but the migration plan still says this is a pure frontend change with no API changes (`design.md:132-144`). That contradiction does not change product behavior, but it weakens traceability for reviewers and integrators.
  SuggestedAction: Update the migration plan to state that the server read model/API response shape is extended without persistence migration.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: verification command usage
  Evidence: `npm test -- --filter FullyQualifiedName~WorkflowSessionSpecs` is not a valid scoped monorepo filter here. It first ran the full .NET suite successfully, then forwarded the .NET filter string into Web/Runner Vitest workspaces where no matching files exist, causing those workspace commands to fail. This is a command-shape issue, not a candidate regression; the relevant server filter was rerun directly with `dotnet test` and passed.
  SuggestedAction: Use direct `dotnet test ... --filter ...` for scoped server tests, or run unfiltered `npm test` when the full monorepo suite is desired.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: verification warnings
  Evidence: Vitest reports a pre-existing config deprecation (`test.poolOptions` removed in Vitest 4) during Web test runs, and Playwright's build logs Rollup annotation warnings from `@microsoft/signalr`. They did not fail the current verification.
  SuggestedAction: Clean up the Vitest config and dependency warning separately.
  Status: pre-existing

<promise>FAIL</promise>
