# Review Report

## Result: FAIL

The change lands the bulk of the contract separation (top-level `expect`, `mohist/opencode` bridge, completion evaluator, profile migration, design doc alignment) and the test suites (server unit 1405, server spec 2869, runner 1126, web 4698) all pass cleanly. Typecheck is clean on every package. The architecture is solid and the design doc is followed faithfully.

However, four blocking issues remain in the post-build candidate snapshot. Each one contradicts an explicit spec scenario or acceptance criterion and cannot be repaired during review without changing public contracts or product behavior.

## Repaired Items

None. Every finding below is a behavior, contract, or spec-compliance change that the repair policy forbids fixing in-line.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/TaskRun.cs:61` (`TaskRunExtensions.ExtractRequiredFiles`)
  Evidence: The marker loop adds every marker `path` (including the `_output` sentinel) to the required-files projection with `CanFetchContent: true`. There is no special-case for `_output`. The built-in `mohist-local.workflow.yaml:181` (`recover:fix-tests`) declares `path: _output`, so when that recovery task is materialized the marker is exposed to `WorkflowStatusMapper.MapTasks` (which calls `ExtractRequiredFiles`) and onwards to evidence/UI as a fetchable file. This directly violates `openspec/changes/issue-408/specs/workflow-task-completion/spec.md` scenario "`_output` is not projected as a file" (`The `_output` sentinel MUST NOT be exposed as a fetchable file path`).
  SuggestedAction: In `ExtractRequiredFiles`, skip `_output` (or emit it with `CanFetchContent: false` plus a flag that marks it as a turn-text requirement). Add a unit test in `TaskRequiredFilesTests` that asserts `_output` never appears as a fetchable path.
  Verification: New unit test feeding `expect.markers = [{ path: "_output", oneOf: [...] }]` should produce no entry with `CanFetchContent: true` for `_output`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Approval.cs:12,68-81` (`DefaultFeedbackTaskUses`, `BuildDefaultFeedbackTask`)
  Evidence: T-001 added `["options"] = JSON.SerializeToElement("${{ vars.agent }}")` to `BuildDefaultFeedbackTask` but left `DefaultFeedbackTaskUses = "mohist/acp-agent"`. The `mohist/acp-agent` handler reads `with.agent`, NOT `with.options` (see `packages/runner/src/actions/acp-agent.ts:36` calling `resolveAgentConfig(context.with)`). So for any profile that omits an explicit `approval.feedback.task` (custom profiles; the fallback path also surfaces through `WorkflowGrain.RequestChangesAsync` → `BuildDefaultFeedbackTask` at `WorkflowGrain.cs:166`), the model-selection binding is inert while the proposal promises "approval feedback task（apply-feedback）...迁移后显式绑定 `options: ${{ vars.agent }}`，开始尊重 issue 级模型选择". The existing unit tests `ApprovalFeedbackTests.cs:97,286` and spec `FeedbackDispatchSpecs.cs:61` lock in `Assert.Equal("mohist/acp-agent", feedbackTask.Uses)`, documenting the bug instead of catching it. Also contradicts the spec requirement "Built-in profiles conform without product-flow drift" (the default feedback task is the implicit built-in).
  SuggestedAction: Change `DefaultFeedbackTaskUses` to `"mohist/opencode"` and update the three locking tests. The `options: ${{ vars.agent }}` binding then reaches an Action that actually reads it. [disallowed:reason: changing the default Action used by every custom profile's feedback loop is a product-behavior change outside the repair policy]
  Verification: Updated `ApprovalFeedbackTests.ResolveFeedbackTask_NullConfig_ReturnsBuiltInDefault` should assert `mohist/opencode`; new test asserts `with.options == "${{ vars.agent }}"` reaches the dispatch envelope.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/WorkflowRoutes.cs:71-89,127` (`POST /api/workflow-runs/{workflowRunId}/tasks/batch`)
  Evidence: `AddTasksRequestTaskDto` only declares `(Id, Title, Uses, With)` — no `Expect`. ASP.NET silently drops the JSON `expect` field. Even if the DTO were fixed, the mapping at lines 79-83 constructs `AddTasksBatchItem(t.Id, t.Title, t.Uses, t.With)` without passing `Expect`, even though `AddTasksBatchItem` (added at `IWorkflowGrain.cs:74-79`) and `WorkflowGrain.AddTasksAsync` (line 383: `WorkflowDispatchHelpers.ParseWith(t.Expect)`) both already support it. The runner's `ServerConnection.addTasks` (`packages/runner/src/server/connection.ts:271`) sends `expect` in the body, and `openspecTasksAction` (`packages/runner/src/actions/openspec.ts:114-119`) calls `mergeTaskExpect` to populate it, but the value is discarded at the HTTP boundary. The runner-side test `openspec-tasks.spec.ts:541 OpenSpecTaskWithTaskLevelExpect_PropagatesExpectIntoAddTaskInput` only mocks `addTasks`; no test exercises the HTTP round-trip. Violates spec scenario "A dynamically generated task uses the canonical declaration" (`generated task SHALL use the same sibling `with` and `expect` fields as a profile task`).
  SuggestedAction: Add `JsonElement? Expect` to `AddTasksRequestTaskDto`; map it via `new AddTasksBatchItem(t.Id, t.Title, t.Uses, t.With, t.Expect)`. Also extend `AddTaskRequestDto` and the single-task endpoint at lines 49-67 for the same reason. Add an HTTP-level spec test that posts `{ tasks: [{ ..., expect: {...} }] }` and asserts the resulting `WorkItem.Expect` is populated.
  Verification: New spec test in `IssueWorkflowProfileApiSpecs` (or sibling) hitting the batch endpoint and asserting `task.Expect` non-null on the subsequent poll.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: `packages/runner/src/runtime/recovery.ts:92-114` (`readAddTasks`)
  Evidence: When `tryRecovery` builds `AddTaskInput` entries for recovery-handler tasks, it copies `with`, `artifacts`, `setVars`, `recovery`, but NOT `expect`. Only the `retrySelf` branch (lines 42-53) propagates `expect`. So if a recovery handler task template declares top-level `expect` (e.g. a future `recover:fix-review-findings` that wants to verify a promise marker in `review.md` before declaring success), the completion contract is silently dropped when the handler task is dispatched. Violates spec requirement "The canonical declaration survives the complete task lifecycle" (`A task's top-level `expect` ... SHALL remain in their corresponding fields through ... automatic `retrySelf`, and runtime task insertion`). The `executor-completion.spec.ts:458-528` tests only exercise `retrySelf`, never the handler-tasks path.
  SuggestedAction: In `readAddTasks`, add `expect: objectField(entry, "expect")` to the `AddTaskInput` construction (mirrors `with`). Add a unit test that constructs a recovery handler task template with `expect` and asserts the resulting `AddTaskInput.expect` is populated.
  Verification: New `tryRecovery` test with `recovery.handlers[0].tasks[0].expect = { files: [...] }`; assert `result.addTasks[0].expect` equals the same shape.
  Status: open

## Blocking Items (Spec-Compliance Gaps)

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:224-244` (`ValidateTaskExpectations`) and `packages/server/src/Mohist.Server/Runner/Services/WorkflowItemTranslator.cs:241-281` (`ValidateLegacyAgentTaskInput`)
  Evidence: The opencode-action-contract spec scenario "Legacy agent input is invalid" requires rejecting `agent`, `kind`, `type`, or Workflow completion policy inside `with`. The implementation only rejects `with.agent` and `with.expect` (when it contains files/markers/failIf). A `mohist/opencode` task with `with.kind: foo` or `with.type: bar` loads and dispatches cleanly; the Action then silently ignores those keys. The spec says validation `SHALL reject the invalid input with the offending field identified`.
  SuggestedAction: Extend `IsInlineAgentUses`-gated validation in both call sites to also reject `kind` and `type` keys for `mohist/opencode` (and optionally `mohist/acp-agent`). Add unit tests for each rejected field.
  Verification: New `WorkflowYamlSerializer` test feeding `uses: mohist/opencode` + `with: { kind: foo }` (and `type: bar`) throws with a field-identifying message.
  Status: open

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/runner/src/actions/expectations.ts:151-161` (`parseLastMarker`)
  Evidence: Design open question (design.md:270) leans toward `_output` supporting both `oneOf` and `contains` forms, but `parseLastMarker` only matches the promise-tag form regardless of how `accepted` was derived. A `_output` marker with `contains: "some literal text"` would have `accepted = ["some literal text"]` and would never match (the literal is not wrapped in the expected tag form). Current built-in profiles only use `oneOf` with promise-tag values, so this is not exercised today, but the lean and the implementation diverge.
  SuggestedAction: Either update the design's open question to "resolved: `_output` requires the promise-tag form" (matching the implementation), or extend `parseLastMarker` to also do a substring scan for `contains`-form values. Document the chosen semantics in `design/workflow/actions.md`.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/runner/src/actions/opencode.ts:96-108` (`opencodeAction` return value)
  Evidence: The bridge handler serializes a rich JSON output (`kind`, `status`, `runtimeSessionId`, `model`, `variant`, `text`, `error`, `providerError`) that `executor.ts:projectTaskOutput` immediately discards for `mohist/opencode` tasks (replaced by `null | { promise }`). The serialization is wasted work and the rich shape contradicts the spirit of the spec ("Runtime and completion facts stay out of OpenCode Action Output"). It is functionally correct (the projection is the wire boundary) but invites confusion for future maintainers who might trust the handler's emitted output.
  SuggestedAction: When the sibling native OpenCode runtime lands, the bridge handler should emit `output: null` directly and rely solely on `turnFact` for the completion fact. Until then, add a doc comment on `opencodeAction` making it explicit that the JSON-output fields are debug-time only and are stripped by the executor.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:264-274` (`IsVerdictMarker`) and `:107-123` (`ValidateExpectVerdictMarkers` error message)
  Evidence: `IsVerdictMarker`'s third disjunct (`normalized.Contains("PASS") && (normalized.Contains("<PROMISE>") || normalized.Contains("</PROMISE>"))`) is hard to read and overlaps with the second disjunct for normal promise shapes. The error message `"Move verdict marker requirements into a check definition"` is stale: after the contract split, a promise verdict is *legal* in `expect.markers[*].oneOf` (built-in profiles do exactly that). The real rule is "use `oneOf`, not `contains`, for verdict markers" — the error text should say so.
  SuggestedAction: Simplify `IsVerdictMarker` to `is "PASS" or "FAIL"` or `contains the promise-tag PASS/FAIL form`. Rewrite the error message to direct authors to use `oneOf` instead of `contains` for promise markers (still under task-level `expect`).
  Status: follow-up

## Test Coverage Gaps

- [ID: item-9]
  Severity: test-gap
  Scope: Server / runner tests around items 1, 3, 4, 5
  Evidence: The four blocking items each survived because the existing test suite does not exercise the boundary:
    - No test in `TaskRequiredFilesTests` feeds a marker with `path: _output`.
    - `openspec-tasks.spec.ts:541` mocks `addTasks` and never hits the HTTP endpoint, so item-3 went unnoticed.
    - `executor-completion.spec.ts:458-528` only tests `retrySelf` expect copy, not handler-task expect copy (item-4).
    - No test feeds `mohist/opencode` with `with.kind`/`with.type` (item-5).
  SuggestedAction: Add the targeted tests described under each item. These are small, focused tests; each one would have caught its corresponding bug.
  Status: open

## Pre-existing or Out-of-scope Items

- [ID: item-10]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:322-342` (`ToCheck`)
  Evidence: `ToCheck` does not call `ValidateTaskExpectations` or `ValidateExpectVerdictMarkers` for check definitions. The spec's validation scenarios are scoped to tasks, so this is consistent with the spec. A `mohist/opencode` *check* (unusual but syntactically allowed) with `with.agent` would slip past the legacy validation. Out of scope for this issue but worth noting if check-level validation is ever tightened.
  SuggestedAction: None for this issue.
  Status: out-of-scope

- [ID: item-11]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueModelMetadata.cs:52` (`ProviderModelFormat` regex `^[^/\s]+/\S+$`)
  Evidence: The regex accepts `provider//model` (empty interior segment) because `\S+` includes `/`. progress.txt:66 calls this out as intentionally permissive — the spec only requires non-empty text before and after the *first* `/`, and `/model` after `provider/` is non-empty. Behavior is spec-compliant; the quirk is documented.
  SuggestedAction: None.
  Status: out-of-scope

<promise>FAIL</promise>
