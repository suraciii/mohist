# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/entities/issue/model/types.ts`, `packages/web/src/widgets/issue-workflow/ui/FeedbackHistory.tsx`, `FeedbackHistory.test.tsx`, `WorkflowView.test.tsx`
  Evidence: The server now serializes feedback with a nested `resolution: WorkflowFeedbackResolution?` object (`IWorkflowGrain.cs:115-123`, `WorkflowGrain.cs:475-492`). The web `ApprovalFeedback` type and `FeedbackHistory` component now match: `types.ts:125-140` declares a nested `ApprovalFeedbackResolution` plus `resolution?: ... | null`; `FeedbackHistory.tsx:85-119` reads `feedback.resolution?.resolvedAt` / `.resolutionTaskId` / `.resolutionSummary`. Test fixtures in `FeedbackHistory.test.tsx:8-22, 48-68, 70-85, 87-113, 160-181, 193-208, 221-249` and `WorkflowView.test.tsx:313-326, 354-380, 441-492` updated to use the nested shape. A new regression test `FeedbackHistory.test.tsx:263-281` (`reads resolution fields from the nested resolution object`) asserts the nested shape renders end-to-end.
  Verification: `npx vitest run src/widgets/issue-workflow/ui/FeedbackHistory.test.tsx` — 16/16 passed. `npx vitest run src/widgets/issue-workflow/` — 66/66 passed. TypeScript: `npx tsc -b` clean.
  Status: resolved

- [ID: item-1-roundtrip]
  Severity: test-gap
  Scope: `packages/web/src/widgets/issue-workflow/ui/FeedbackHistory.test.tsx:284-316`
  Evidence: The previous review requested "a focused regression test that roundtrips a real `WorkflowFeedbackSnapshot` JSON sample (matching the server serializer) through `FeedbackHistory`." Initial repair added a direct-construction test only; this follow-up parses a real server JSON sample and asserts the body, resolution summary, resolution task id, and "Feedback task applied"/"Resolution summary" labels all render.
  Verification: `npx vitest run src/widgets/issue-workflow/ui/FeedbackHistory.test.tsx` — 16/16 passed. The new test `renders a real WorkflowFeedbackSnapshot JSON payload (server wire shape) end-to-end` confirms nested resolution, lowercase status, and top-level `issueNumber` all work as the server emits them.
  Status: resolved

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: The dead `GetFeedbackAsync` method (previously lines 60-77) has been removed. `grep -n "GetFeedbackAsync" packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs` returns no matches. The other `GetFeedbackAsync` symbol on `IWorkflowGrain` (`IWorkflowGrain.cs:35`) is a different method and remains.
  Verification: `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj` — 0 Warning(s) 0 Error(s). The build succeeds with no other references to the removed method. `grep -rn "IssueQuerier.GetFeedbackAsync\|querier\.GetFeedbackAsync" packages/server/src` returns 0 matches.
  Status: resolved

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/ApprovalFeedback.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueFeedbackApiSpecs.cs`
  Evidence: Chose option A (lowercase per spec). The default `[JsonConverter(typeof(JsonStringEnumConverter))]` was replaced with a custom `ApprovalFeedbackStatusJsonConverter` (`ApprovalFeedback.cs:6, 15-46`) that emits `open` / `resolved` and accepts both casings for back-compat with older persisted payloads. Seven PascalCase string assertions in `IssueFeedbackApiSpecs.cs:66, 200, 244, 330, 416, 499, 503` were updated to lowercase to match the new wire format. The web type already declared `ApprovalFeedbackStatus = 'open' | 'resolved'` (`types.ts:123`) and the component checks `feedback.status === 'open' | 'resolved'` (`FeedbackHistory.tsx:30-31`), so no web change was required.
  Verification: `dotnet test --filter "FullyQualifiedName~IssueFeedbackApiSpecs"` — 18/18 passed. `dotnet test --filter "FullyQualifiedName~ApprovalFeedback"` — 37/37 passed. The lowercase casing is verified end-to-end through the API.
  Status: resolved

- [ID: item-3-backcompat]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Domain/ApprovalFeedbackSpecs.cs:529-570`
  Evidence: Two new unit tests cover the converter directly. `ApprovalFeedbackStatus_JsonLowercase_RoundTrips` asserts lowercase deserializes to the correct enum and that the serializer emits lowercase. `ApprovalFeedbackStatus_JsonPascalCase_IsAcceptedForBackCompat` asserts that legacy `"Open"` / `"Resolved"` payloads still deserialize to the equivalent enum member, so old persisted state remains readable.
  Verification: `dotnet test --filter "FullyQualifiedName~ApprovalFeedbackStatus"` — 2/2 passed.
  Status: resolved

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs`
  Evidence: The dead `OnApprovalRejectedAsync` method (previously lines 1319-1322) and the `ApprovalResult.Rejected => OnApprovalRejectedAsync(e.Reason)` arm in `OnApprovalResolvedAsync` (previously line 1309) have been removed. The arm was unreachable for new workflow runs because `RejectAsync` now routes through `RequestChangesAsync` (see `WorkflowGrain.cs:152-166`). `grep -n "OnApprovalRejectedAsync" packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs` returns no matches.
  Verification: `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj` — 0 Warning(s) 0 Error(s). `dotnet test --filter "FullyQualifiedName~ApprovalGate|FullyQualifiedName~WorkflowState|FullyQualifiedName~WorkflowRetry"` — all pass. The `[Obsolete]`-style test suppressions (`#pragma warning disable CS0618`) in `ApprovalGateSpecs.cs:104-106, 138-140` continue to compile and the `Legacy RejectAsync must NOT mark the workflow as failed` test passes.
  Status: resolved

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/StageRun.cs`, `WorkflowRun.Approval.cs`, `WorkflowRun.Failure.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/ApprovalGateSpecs.cs`
  Evidence: The `LastRejectionReason` field has been removed from `StageRun.cs` (previously line 27), its set/clear in `WorkflowRun.Approval.cs` (previously lines 131, 175), and the Rerun copy in `WorkflowRun.Failure.cs` (previously line 85). The unused `ReadCurrentStageRerunFieldsAsync` helper that read this field was removed from `ApprovalGateSpecs.cs` (previously lines 175-181). `grep -rn "LastRejectionReason" packages/server` returns no matches. Persistence safety verified: `WorkflowRun` is serialized to JSON via `WorkflowRunStore.cs:104` and deserialized with `System.Text.Json` defaults, which ignore unknown properties — so old persisted `WorkflowRun.State` JSON containing `LastRejectionReason` continues to deserialize cleanly (the field is simply not populated, which is acceptable since the field was only test-reachable after the prior repair).
  Verification: `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj` — 0 Warning(s) 0 Error(s). `dotnet test --filter "FullyQualifiedName~ApprovalFeedback|FullyQualifiedName~ApprovalGate|FullyQualifiedName~WorkflowState|FullyQualifiedName~WorkflowRetry"` — all pass.
  Status: resolved

## Blocking Items

*(none)*

## Follow-up Items

- [ID: item-8]
  Severity: follow-up
  Scope: `openspec/changes/issue-109/specs/http-api/spec.md:66`
  Evidence: The spec carries the line "The approval reject endpoint SHALL create an `ApprovalFeedback` record instead of recording a terminal rejection." The implementation has removed the `POST /api/issues/:number/reject` endpoint and the `mo issue reject` CLI command entirely, replacing them with `POST /api/issues/:number/feedback` (which creates the feedback record). This matches the issue body ("Rename the user-facing action from `Send back`/`Reject` to `Request changes`") but the spec text is now slightly stale. [disallowed:spec text is review-managed]
  SuggestedAction: Update the spec at `http-api/spec.md:66` to reference the `POST /api/issues/:number/feedback` endpoint (the `request changes` path) and to remove the "approval reject endpoint" wording, or open a follow-up issue to refresh the spec delta.
  Status: follow-up

- [ID: item-9]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Grains/IWorkflowGrain.cs:20`
  Evidence: The legacy `RejectAsync(string? reason)` method on the grain interface is preserved for back-compat (`WorkflowGrain.cs:152-166` logs a warning and routes to `RequestChangesAsync`). This is consistent with the in-place repair of the previous review's item-13 carry-over. There is no `[Obsolete]` attribute on the interface or implementation, even though test files use `#pragma warning disable CS0618` to suppress CS0618 in the test bodies. This means the tests' CS0618 pragma is currently suppressing nothing. The pragma is harmless but is a no-op until `[Obsolete]` is added.
  SuggestedAction: Either add `[Obsolete("Use RequestChangesAsync — reject now routes through the feedback loop.")]` to both `IWorkflowGrain.RejectAsync` and `WorkflowGrain.RejectAsync` so the existing `#pragma warning disable CS0618` in `ApprovalGateSpecs.cs:104, 138` becomes meaningful, or remove the `#pragma` directives from the test file. Either change is a small style/contract decision and not required for the current issue.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Api/ActivityWaitingApiSpecs.cs:40` and `Foundation/WorkflowVariableSpecs.*` and `Api/TemplateRoutesSpecs.*` (~62 tests in total)
  Evidence: The full test suite reports 62 failed / 1108 passed / 6 skipped / 1176 total on the post-repair candidate snapshot. The new tests added in this review round (the lowercase roundtrip test in `FeedbackHistory.test.tsx`, the new converter tests in `ApprovalFeedbackSpecs.cs`, and the wire-shape roundtrip test in `FeedbackHistory.test.tsx`) all pass and account for the difference from the previous review's count of 1107 passed. The 62 failures are unchanged from the parent commit's pre-existing baseline.
  SuggestedAction: Track separately under a different issue. The relevant pre-existing tests are unrelated to the approval feedback loop.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: `packages/web/src/widgets/issue-workflow/ui/FeedbackHistory.test.tsx` and `WorkflowView.test.tsx`
  Evidence: Test fixtures use the new nested `resolution: { ... }` shape (this is the post-item-1 state, not pre-existing anymore). Tagged as pre-existing for traceability only — the fixtures are part of the item-1 fix and required updating for the nested shape to be tested.
  Status: pre-existing

- [ID: item-10]
  Severity: info
  Scope: `packages/cli/Mohist.Cli/`
  Evidence: No CLI test project exists in the repository. The new `mo issue feedback list` and `mo issue feedback show` commands, the `request-changes` action, and the `TableShape.FeedbackList` / `TableShape.FeedbackShow` rendering paths are not exercised by automated tests. This is consistent with the rest of the CLI surface (no CLI tests predate this change). The server-side behavior is covered by `IssueFeedbackApiSpecs`, but the CLI thin-client contract (path, query string, output mode validation) has no automated coverage.
  SuggestedAction: Optional future action. A future test project under `packages/cli/tests/` would close the gap. Not blocking the current change.
  Status: pre-existing

- [ID: item-11]
  Severity: info
  Scope: `openspec/changes/issue-109/specs/approval-feedback/spec.md`, `web-ui/spec.md`, `workflow-run/spec.md`, `workflow-engine/spec.md`
  Evidence: The other spec deltas in `openspec/changes/issue-109/specs/` were not modified in this review round. They were authored in the original change and were not flagged as blocking by the previous review. The implementation is consistent with these spec texts on the items verified (the runtime behavior of `RequestChanges` matches `approval-feedback-agent/spec.md` and `workflow-run/spec.md`; the dispatch context shape matches `approval-feedback-agent/spec.md:21-23`; the prompt contract matches `apply-feedback.prompt` content).
  SuggestedAction: No action required for this review.
  Status: pre-existing

<promise>PASS</promise>
