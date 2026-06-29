# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs`
  Evidence: Generic `agent-launch` activity cards without an issue reference still expose issue-number-zero fields. `ToActivityCard` reads `IssueNumber(record)` before the generic branch and passes `issueNumber` / `issueTitle` into `ActivityCardDto` at `AgentSessionQuerier.cs:901` and `AgentSessionQuerier.cs:913-916`; `IssueNumber` falls back to `0` at `AgentSessionQuerier.cs:1072-1075`, and `IssueTitle` falls back to `Issue #0` at `AgentSessionQuerier.cs:976-979`. This violates `agent-session-visibility/spec.md:140-152` and `http-api/spec.md:62-73`, which forbid issue-number-zero identity for generic sessions with no issue ref. The regression test currently locks in the wrong behavior by asserting `issueNumber == 0` at `AgentSessionActivityVisibilitySpecs.cs:68-70`. [disallowed:public-contract-change]
  SuggestedAction: Change the generic activity shape so no-issue generic sessions do not publish issue-zero identity. Either make issue association fields nullable for generic cards or add separate nullable association fields while keeping primary attribution agent-based. Update tests to assert no issue-zero identity.
  Verification: Add/adjust an integration test for a generic session with no `mohist.io/agent-launch/issue-number` label that rejects `issueNumber: 0`, `Issue #0`, and any `issue_{projectId}_0` identity.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs`
  Evidence: Generic activity association with an issue is derived from the workflow label `mohist.io/issue-number`, not the launch context label. `ToActivityCard` uses `IssueNumber(record)` at `AgentSessionQuerier.cs:901`; `IssueNumber` only reads `AgentSessionQueryMetadataKeys.IssueNumber` at `AgentSessionQuerier.cs:1072-1075`. Real direct Agent launches stamp issue context as `GenericAgentSessionMetadata.IssueNumber` (`mohist.io/agent-launch/issue-number`) in `GenericAgentSessionMetadata.cs:47` and `GenericAgentSessionMetadata.cs:79-80`. The test masks this by seeding the workflow label at `AgentSessionActivityVisibilitySpecs.cs:166-168`. This means real generic sessions with issue context will not be associated in activity, contrary to `agent-session-visibility/spec.md:154-158` and `http-api/spec.md:62`.
  SuggestedAction: In the generic activity branch, derive issue association from `GenericAgentSessionMetadata.IssueNumber`. Update test fixtures to seed the actual agent-launch label and keep workflow-label behavior isolated to workflow sessions.
  Verification: Add an integration test where a generic session has only `mohist.io/agent-launch/issue-number`; assert the activity card carries that issue association and remains agent-attributed.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs`
  Evidence: Agent-scoped list and generic summary can omit `resolvedModel` even though the session state has a resolved model. The list uses only transcript summaries at `AgentSessionQuerier.cs:185-199`, and summary uses only `summary.ResolvedModel` at `AgentSessionQuerier.cs:523-543`. Model resolution is persisted on `session.Settings.Model` by `AgentSession.Transitions.cs:31` and `AgentSession.Transitions.cs:50`, so a session without a model transcript part returns null/omitted `resolvedModel`. This violates `http-api/spec.md:5-12` and `http-api/spec.md:31-39`.
  SuggestedAction: Use `summary?.ResolvedModel ?? s.Settings.Model` in list and `summary.ResolvedModel ?? session.Settings.Model` in summary.
  Verification: Add list and summary tests for a generic session whose state has `settings.model` but whose transcript has no model event.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionReadModels.cs`
  Evidence: `GenericAgentSessionSummaryDto.ToolCallCount` and `ToolErrorCount` are nullable at `AgentSessionReadModels.cs:250-252`; `TranscriptEventSummaryProjector` returns null for zero counts at `TranscriptEventSummaryProjector.cs:43-44`; server JSON omits nulls via `JSON.cs:11-14`. A generic summary with no tool calls therefore omits the required count fields instead of returning `0`, violating `agent-session-visibility/spec.md:114-118` and `http-api/spec.md:31-39`.
  SuggestedAction: Make the summary count fields non-nullable or coalesce null to `0` in `GetGenericSessionSummaryAsync`.
  Verification: Add a zero-tool summary test that asserts `toolCallCount: 0` and `toolErrorCount: 0` are present in the serialized response.
  Status: open

- [ID: item-5]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowActivityQuerier.cs`
  Evidence: The active-agents readout treats any row with `AgentSessionId != null` as active at `WorkflowActivityQuerier.cs:117`. `AgentSessionStore.ToRow` preserves `AgentSessionId` and stores `Status = "bound"` whenever a runtime id exists at `AgentSessionStore.cs:109-117`, and a terminal `session.closed` fact does not clear it. Closed generic sessions can therefore continue appearing in `/agent/status` as active, contrary to `agent-session-visibility/spec.md:160-174` and `http-api/spec.md:80-94`, which require currently active generic sessions. Existing tests cover inclusion of an active generic session but not terminal exclusion.
  SuggestedAction: Exclude generic sessions with terminal `session_closed` facts before emitting `ActiveAgentDto`, reusing the terminal-fact logic already used by session list/summary.
  Verification: Add a regression test with a generic session that has `AgentSessionId` plus a terminal transcript event and assert it is absent from `/api/projects/{project}/agent/status`.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionReadModels.cs`
  Evidence: The generic summary context references omit project context. `GenericAgentSessionSummaryContextRefsDto` only includes issue, epic, repository, and workspace path at `AgentSessionReadModels.cs:284-288`, and `BuildGenericSessionSummaryContextRefs` builds only those fields at `AgentSessionQuerier.cs:564-578`. The accepted spec requires summary context refs to surface issue, epic, project, repository, and workspace path at `agent-session-visibility/spec.md:95-124`. [disallowed:public-contract-change]
  SuggestedAction: Either add the project id to the summary context refs/top-level summary or formally update the spec/design to state project is represented only by the route scope.
  Verification: Add or update a summary contract test that asserts the decided project context behavior.
  Status: open

- [ID: item-7]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs` and `packages/cli/Mohist.Cli/MohistCliApi.cs`
  Evidence: CLI JSON mode for `mo agent session list`, `show`, and `transcript` does not print the raw server payload. These commands call `PrintWithOutputAsync` at `MohistCliCommands.Agent.cs:608-611`, `MohistCliCommands.Agent.cs:651-655`, and `MohistCliCommands.Agent.cs:695-699`. `PrintEnvelopeAsync` routes JSON responses to `PrintResponseAsync` unless `rawJson` is set at `MohistCliApi.cs:777-786`, and `PrintResponseAsync` prints only `node["data"]` at `MohistCliApi.cs:1022-1027`. The CLI spec requires raw server payload without omission at `cli-interface/spec.md:20-23`, `cli-interface/spec.md:43-46`, and `cli-interface/spec.md:73-76`. Existing tests named `EmitsRawPayload` only assert nested data fields at `CliAgentSessionCommandSpecs.cs:815-845`, `CliAgentSessionCommandSpecs.cs:980-1004`, and `CliAgentSessionCommandSpecs.cs:1056-1080`.
  SuggestedAction: Add raw-response support for these GET commands or a dedicated helper that uses `PrintRawResponseAsync` for JSON mode.
  Verification: Update CLI JSON tests to assert top-level `success` and `data` are present, preferably with a sentinel top-level field to prove the envelope is not stripped.
  Status: open

- [ID: item-8]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs`
  Evidence: `mo agent session list <name>` handles an unknown name locally instead of surfacing the server 404 from the session-list endpoint. `BuildSessionList` resolves names by calling `ResolveAgentAsync` before the list request at `MohistCliCommands.Agent.cs:596-611`; `ResolveAgentAsync` lists `/agents?all=true` and prints `Agent 'nope' not found` locally at `MohistCliCommands.Agent.cs:800-812`. The CLI spec's unknown-agent scenario requires the server 404 and server-provided error for `mo agent session list nope` at `cli-interface/spec.md:25-31`. The test locks in the client-only behavior at `CliAgentSessionCommandSpecs.cs:879-902`.
  SuggestedAction: Let the list endpoint resolve `{agentRef}` for this command, or fall back to `GET .../agents/{agentRef}/sessions` when local name resolution fails so the server-provided 404 is surfaced.
  Verification: Replace or extend the unknown-name CLI test to assert the session-list endpoint returns the 404 body and the CLI exits non-zero with that server error.
  Status: open

- [ID: item-9]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260629112745_AddAgentLaunchLabelComputedColumns.cs`
  Evidence: The migration does not follow the explicitly required SQLite add-nullable-then-`AlterColumn` rebuild pattern. The accepted design calls for that pattern at `design.md:246-250` and `design.md:260-266`, but this migration directly uses `AddColumn(... computedColumnSql, stored: true)` for all six columns at `20260629112745_AddAgentLaunchLabelComputedColumns.cs:32-78`. The migration test says it confirms the add-then-AlterColumn path at `AgentLaunchLabelComputedColumnsMigrationSpecs.cs:99-101`, but no such path exists in the migration.
  SuggestedAction: Rewrite `Up` to use the established add-nullable, then `AlterColumn` to stored computed pattern, or update the design/spec and test comments if direct `AddColumn` is now the intended supported pattern.
  Verification: Run the migration test suite after the rewrite and inspect the generated migration to ensure it matches the documented pattern.
  Status: open

- [ID: item-10]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs` and query tests
  Evidence: The spec says agent identity and all `agent-launch/*` context labels are first-class indexed query keys at `agent-session-visibility/spec.md:20-28` and `agent-session-visibility/spec.md:42-47`. The model creates actual indexes only for `(LabelAgentId, LabelProjectId, CreatedAt)`, `LabelAgentLaunchIssueNumber`, and `LabelAgentLaunchEpicNumber` at `MohistDbContext.cs:166-174`; it does not index `LabelAgentName`, `LabelAgentLaunchRepository`, or `LabelAgentLaunchWorkspacePath` even though `QueryRowsByLabels` maps those keys at `AgentSessionQuery.cs:120-125`. `AgentSessionQuerySpecs.cs:140-159` claims repository/workspace resolve via indexed columns but only proves rows are returned, not index usage.
  SuggestedAction: Clarify whether "indexed" means stored computed columns or actual SQLite indexes. If actual indexes are required, add indexes for agent-name, repository, and workspace path; otherwise update the spec/test wording to avoid promising index usage.
  Verification: Add `EXPLAIN QUERY PLAN` or index metadata assertions for every label that must be backed by a real index.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: info
  Scope: verification
  Evidence: `npm run build` passed. `npm test` exceeded the 120s tool timeout while server tests were still running, so the full repo test result is unknown from this review run. Targeted changed suites passed: server AgentSession review suites passed 49 tests, and `CliAgentSessionCommandSpecs` passed 50 tests. These passing targeted tests do not clear the review because several tests currently assert or omit the problematic behaviors described above.
  SuggestedAction: Re-run the full `npm test` with a longer timeout after fixing the blocking items.
  Status: out-of-scope

<promise>FAIL</promise>
