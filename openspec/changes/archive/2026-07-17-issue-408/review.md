# Review Report

## Result: PASS

The post-repair candidate snapshot (commit `9dcdf6de4`) resolves all eight findings from the prior FAIL review. The change lands the full contract separation: top-level `expect` beside `with`, Workflow-owned completion evaluation with `_output` private fact channel, `mohist/opencode` bridge handler with minimal promise projection, legacy-shape rejection (`with.expect`/`with.agent`/`with.kind`/`with.type`) at both profile-load and dispatch time, HTTP-level `expect` propagation through `/tasks/batch` and `/tasks`, recovery handler-task `expect` propagation, default feedback task migration to `mohist/opencode`, and design-doc alignment. All test suites pass cleanly (server unit 1406, server spec 2873, runner 1127, build 0 warnings / 0 errors).

## Repaired Items

All repairs were applied by the fix-review-findings task (commit `9dcdf6de4`) prior to this review. They are verified here as part of the post-repair snapshot.

- [ID: item-1]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/TaskRun.cs:57,89`
  Evidence: `_output` sentinel is now skipped in `ExtractRequiredFiles` via `if (string.Equals(path, OutputMarkerPath, StringComparison.Ordinal)) continue;`. The constant `OutputMarkerPath = "_output"` is declared with a doc comment explaining the invariant. New unit test `ExtractRequiredFiles_OutputMarkerPath_IsNotProjectedAsFile` in `TaskRequiredFilesTests.cs:88-111` feeds both `_output` and `review.md` markers and asserts only `review.md` appears.
  Verification: `dotnet test --filter "FullyQualifiedName~ExtractRequiredFiles_OutputMarkerPath_IsNotProjectedAsFile"` — passed (part of 1406/1406 unit tests).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Approval.cs:12`
  Evidence: `DefaultFeedbackTaskUses` changed from `"mohist/acp-agent"` to `"mohist/opencode"`. Three locking tests updated: `ApprovalFeedbackTests.cs:97` (`RequestChanges_SchedulesApplyFeedbackRuntimeTask_WithInvalidateChecks`), `ApprovalFeedbackTests.cs:286` (`RequestChanges_WithoutFeedbackTaskOverride_UsesBuiltInApplyFeedbackDefault`, now also asserts `with.options == "${{ vars.agent }}"`), `ApprovalFeedbackTests.cs:303` (`ResolveFeedbackTask_NullConfig_ReturnsBuiltInDefault`, now also asserts `with.options`), and `FeedbackDispatchSpecs.cs:61`.
  Verification: `dotnet test --filter "FullyQualifiedName~RequestChanges_Schedules|FullyQualifiedName~ResolveFeedbackTask_NullConfig|FullyQualifiedName~RequestChanges_WithoutFeedbackTaskOverride|FullyQualifiedName~AwaitingApproval_RequestChanges_FeedbackTaskDispatch"` — all passed.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Api/WorkflowRoutes.cs:60-67,80-85,127-141`
  Evidence: `AddTasksRequestTaskDto` and `AddTaskRequestDto` both gained `JsonElement? Expect = null`. Both endpoint mappings pass `Expect` through (`new AddTasksBatchItem(t.Id, t.Title, t.Uses, t.With, t.Expect)` and `new RuntimeTaskInput(..., Expect: request.Expect)`). New HTTP spec test `TasksBatch_PostWithExpect_PropagatesExpectIntoMaterializedTaskRun` in `WorkflowRunControlApiSpecs.cs:434-477` posts to `/api/workflow-runs/{wrId}/tasks/batch` with an `expect` body and asserts `dynamicTask.ExpectInput` contains `files` and `markers`.
  Verification: `dotnet test --filter "FullyQualifiedName~TasksBatch_PostWithExpect"` — passed (part of 2873/2873 spec tests).
  Status: resolved

- [ID: item-4]
  Severity: info
  Scope: `packages/runner/src/runtime/recovery.ts:106-110`
  Evidence: `readAddTasks` now includes `expect: objectField(entry, "expect")` in the `AddTaskInput` construction, mirroring `with`. New test `PropagatesExpectFromHandlerTaskTemplate_NotJustSelfRetry` in `executor-completion.spec.ts:530-588` constructs a recovery handler task with `expect.markers[{path:"review.md", oneOf:[...], failIf:...}]` and asserts `result.addTasks[0].expect` equals the same shape.
  Verification: `npm run test:run -w packages/runner` — 1127/1127 passed (includes the new test).
  Status: resolved

- [ID: item-5]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:246-265` and `packages/server/src/Mohist.Server/Runner/Services/WorkflowItemTranslator.cs:266-285`
  Evidence: Both validators now reject `with.kind` and `with.type` for inline-agent tasks (`mohist/opencode` and `mohist/acp-agent`). New tests: `WorkflowYamlParser_InlineAgentWithLegacyDiscriminator_RejectsWithFieldIdentifyingError` (Theory with `kind` and `type`) and `WorkflowYamlParser_NonInlineAgentWithKindAndType_IsAcceptedAsActionOwnedInput` in `MohistLocalWorkflowProfileSpecs.cs:636-690`.
  Verification: `dotnet test --filter "FullyQualifiedName~InlineAgentWithLegacyDiscriminator|FullyQualifiedName~NonInlineAgentWithKindAndType"` — passed.
  Status: resolved

- [ID: item-6]
  Severity: info
  Scope: `openspec/changes/issue-408/design.md:272-274` and `design/workflow/actions.md:96-99`
  Evidence: Design open question moved from "Open Questions" to "Resolved Questions": `_output` requires the promise-tag form. `design/workflow/actions.md` documents the chosen semantics: `_output` only recognizes promise-tag form, last-occurrence wins, no file-system read, no evidence projection as a file path.
  Verification: Design doc reviewed; no code change needed — implementation already matches the resolution.
  Status: resolved

- [ID: item-7]
  Severity: info
  Scope: `packages/runner/src/actions/opencode.ts:60-67`
  Evidence: Doc comment added to `opencodeAction` clarifying that the rich diagnostic JSON in `output` is debug-time only and is stripped by `projectTaskOutput` to `null | { promise }` per the opencode-action-contract spec.
  Verification: `npm run typecheck -w packages/runner` — clean.
  Status: resolved

- [ID: item-8]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:117-123,286-293`
  Evidence: `IsVerdictMarker` simplified — removed the unreadable third disjunct (`normalized.Contains("PASS") && (normalized.Contains("<PROMISE>") || normalized.Contains("</PROMISE>"))`). Now just `is "PASS" or "FAIL"` or `contains the promise-tag PASS/FAIL form`. Error message rewritten from stale "Move verdict marker requirements into a check definition" to "Use 'oneOf' (not 'contains') for promise verdict markers under task-level 'expect', or move non-verdict literal markers into a check definition." Locking test assertion updated from `"check definition"` to `"oneOf"` in `MohistLocalWorkflowProfileSpecs.cs:630`.
  Verification: `dotnet test --filter "FullyQualifiedName~WorkflowYamlParser_TaskWithVerdictMarkerInExpect"` — passed.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-9]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:251-265` and `packages/server/src/Mohist.Server/Runner/Services/WorkflowItemTranslator.cs:271-285`
  Evidence: The `kind`/`type` rejection error message always says "The 'mohist/opencode' Action is selected by 'uses'" even when the actual `uses` is `mohist/acp-agent`. This is technically inaccurate for the `mohist/acp-agent` case. The inaccuracy is forward-looking (the operator should migrate to `mohist/opencode`), so it does not cause incorrect behavior, but a more neutral phrasing like "The selected inline-agent Action does not read 'kind'" would be accurate for both identifiers.
  SuggestedAction: Replace the hardcoded `'mohist/opencode'` reference in the `kind`/`type` error messages with the actual `uses` value or a neutral inline-agent reference.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Runner/Services/WorkflowItemTranslator.cs:266-285` (dispatch-time `kind`/`type` rejection)
  Evidence: The YAML-level `kind`/`type` rejection has a dedicated unit test (`WorkflowYamlParser_InlineAgentWithLegacyDiscriminator_RejectsWithFieldIdentifyingError`), but the dispatch-time validator (`ValidateLegacyAgentTaskInput`) does not have a dedicated test that exercises the `kind`/`type` path. The dispatch validator is tested indirectly via the `with.agent` and `with.expect` rejection paths, but no test feeds a persisted `mohist/opencode` work item with `with.kind` or `with.type` through `BuildTaskDispatchAsync`. The risk is low because both validators use the same `IsInlineAgentUses` gate and `ContainsKey` check, but a focused test would catch future divergence.
  SuggestedAction: Add a spec test that constructs a `WorkItem` with `Uses = "mohist/opencode"` and `With` containing a `kind` or `type` key, calls `TranslateToDispatchAsync`, and asserts the `InvalidOperationException` names the offending field.
  Status: follow-up

- [ID: item-11]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/TaskRun.cs:89` (`ExtractRequiredFiles` `_output` skip)
  Evidence: The `_output` sentinel is skipped entirely from the required-files projection (`WorkflowTaskRequiredFile` list). The spec's hard requirement ("MUST NOT offer `_output` as file content to fetch") is satisfied. The softer requirement ("task evidence SHALL treat it as a turn-text requirement") is not surfaced — `WorkflowTaskRequiredFile` has no field to distinguish a turn-text requirement from a file. The evidence projection thus omits the `_output` requirement silently. This does not affect completion evaluation (the runner evaluator reads `expect` directly), but a UI that wants to show "this task has a turn-text completion requirement" would have no data to render.
  SuggestedAction: If future UI needs require distinguishing turn-text requirements in evidence, add a `Kind` or `IsTurnText` field to `WorkflowTaskRequiredFile` and emit a non-fetchable entry for `_output`. Until then, the current skip is spec-compliant.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-12]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:341-361` (`ToCheck`)
  Evidence: `ToCheck` does not call `ValidateTaskExpectations` for check definitions. The spec's validation scenarios are scoped to tasks, so this is consistent. A `mohist/opencode` check with `with.agent` would slip past validation. Out of scope for this issue.
  SuggestedAction: None for this issue.
  Status: out-of-scope

- [ID: item-13]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueModelMetadata.cs:52` (`ProviderModelFormat` regex)
  Evidence: The regex `^[^/\s]+/\S+$` accepts `provider//model` (empty interior segment) because `\S+` includes `/`. Spec only requires non-empty text before and after the first `/`; `/model` after `provider/` is non-empty. Behavior is spec-compliant.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-14]
  Severity: info
  Scope: `packages/runner/src/actions/registry.ts:57` (`mohist/acp-agent` still registered)
  Evidence: The issue body says "该 Action 已移除" (that Action has been removed), but the runner still registers `mohist/acp-agent`. The spec's validation scenarios only require rejecting legacy `with` shapes (`with.expect`/`with.agent`/`with.kind`/`with.type`), not the Action identifier itself. The design D10 clarifies the bridge handler is temporary and ACP will be removed entirely in the sibling runtime issue. A clean `mohist/acp-agent` task (without legacy fields) would still execute; this is accepted as a transitional path. All built-in profiles have migrated to `mohist/opencode`.
  SuggestedAction: None for this issue. The sibling runtime issue in epic 46 removes ACP entirely.
  Status: out-of-scope

<promise>PASS</promise>
