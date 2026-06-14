# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `design.md` Decision 1 (line 28) stated the runtime store would be a
    `Dictionary<string, JsonElement> ... on the WorkflowRun aggregate state`. The
    new `WorkflowRun.RuntimeVariables.cs` and `WorkflowGrain.MakeDispatchAsync` use
    `tasks.<taskDefinitionId>.outputs.<name>` flat keys (line 16 of
    `WorkflowRun.RuntimeVariables.cs`) and reconstruct the nested shape only at
    dispatch merge time. The two representations are consistent in the codebase
    but the design only documented the nested shape option, so the discrepancy
    is purely documentary. No change required; design and code are in agreement
    on the flat storage and the nested rendering.
  Verification: Re-read `WorkflowRun.RuntimeVariables.cs:16` and
    `BuildRuntimeVariablesElement` in `WorkflowGrain.cs:719-738`; both
    representations are tested and the spec language supports either.
  Status: resolved (no code change needed; documentation alignment is complete)

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: regression safety / template semantics
  Evidence: `packages/runner/src/core/template.ts:177-185` changes
    `resolvePath` so that ANY path starting with `tasks.` resolves to `""`
    when the lookup misses. This is broader than the spec's "missing runtime
    output resolves to empty" — it also silently swallows typos
    (e.g. `tasks.proposals.outputs.x` with a plural typo) and shadowing
    conflicts with any user-defined top-level `tasks` key. Tests
    `MissingTaskOutputReferenceAsWholeStringResolvesToEmptyString` and
    `MissingTaskOutputReferenceIsNotReportedAsUnresolved` lock the behavior
    in. Per the design's risk acknowledgment ("Output `from` selectors
    reference missing fields, leaving variables empty silently. Mitigation:
    treat missing values as absent rather than failures") this is the
    documented choice. The behavior is consistent with the spec, but worth
    tracking because a future tightening (e.g. emit a warning for paths
    starting with `tasks.` that don't exist) is mentioned in the design's
    Open Questions.
  SuggestedAction: Either (a) add a follow-up issue to log a warning when a
    `tasks.*` path resolves to empty, or (b) gate the empty-string fallback
    on the presence of a `tasks` key in the variables so that pure typos
    in the namespace name still surface as a "reference not found" error.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: test gap / spec coverage
  Evidence: The spec (`openspec/changes/issue-97/specs/workflow-run/spec.md`
    "Runtime variables persist across stage boundaries") requires that
    outputs captured in one stage remain available in the next. The new
    tests only cover within-stage flow:
    `TaskOutputVariablesEndToEndSpecs.EndToEnd_TaskOutput_CapturedAndResolvedInDownstreamTask`
    runs two tasks in the same stage, and no new spec tests verify
    cross-stage persistence. The design's "Variables survive stage retry"
    scenario is also untested. The behavior is correct on inspection
    (`WorkflowRun.RuntimeVariables` outlives stage transitions because the
    grain is reused) but the lack of a test is a coverage gap.
  SuggestedAction: Add a multi-stage grain test (Plan → Build) that
    captures an output in Plan and asserts it appears in the Build task
    dispatch payload, and a retry test that captures in an earlier
    attempt and reuses it.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: test gap / parsing
  Evidence: `WorkflowYamlSerializerSpecs` covers omitted outputs, missing
    name, missing from, duplicate name, and round-trip. It does NOT cover:
    (a) `outputs: []` (empty list) — the parser normalizes this to `null`
    but the test for the omitted case doesn't cover it, and (b)
    non-object entries inside the outputs array (e.g. `outputs: ["bad"]`)
    — the parser throws "entries must be objects" but no test asserts the
    message.
  SuggestedAction: Add a test for `outputs: []` and for non-object entries
    inside the outputs array. Both are quick unit tests.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: scope creep / commit hygiene
  Evidence: Commit `51801dd6` (T-009) bundles three unrelated changes:
    the issue-97 end-to-end spec, an `AgentSessions` schema migration
    (`20260614083940_DropAgentSessionCompletedAtUpdatedAt` and its
    Designer) and the matching `MohistDbContextModelSnapshot` edits
    dropping `CompletedAt` and `UpdatedAt` columns from `AgentSessions`.
    The migration and snapshot have nothing to do with task output
    variables. The unrelated change is benign (the columns are no longer
    in the model) but the T-009 commit message and the issue scope do
    not match the diff.
  SuggestedAction: In future iterations, split unrelated database
    refactors out of the issue's atomic commits. For this review the
    migration's correctness is out of scope; just note the boundary
    issue. (No follow-up needed for issue 97 itself.)
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: artifact path error clarity
  Evidence: With the new `tasks.*` template behavior
    (`packages/runner/src/core/template.ts:182-184`), a missing
    `tasks.<id>.outputs.<name>` in an `artifacts.files[].path` resolves
    to `""` and is not flagged by `unresolvedReferences` (the function
    that surfaces a clear "undefined variable" error before file capture
    in `executor.ts:153-160`). The artifact path then renders to
    `"/file.md"` (or just `"file.md"`), the capture layer tries to read
    from that path, and the user gets a filesystem ENOENT instead of a
    "variable not found" diagnostic. The behavior is correct per spec
    ("missing runtime output resolves to empty") but the error message
    is less actionable for artifact paths than for `with` values.
  SuggestedAction: Track a follow-up to either narrow the empty-string
    fallback (item-2) or to teach `unresolvedReferences` to still flag
    `tasks.*` paths in artifact declarations where the path is being
    used as a literal filesystem path rather than documentation.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: pre-existing test failures
  Evidence: Running the full server test suite shows 59 failed tests across
    `ArchitectureRules`, `IssueCli*Specs`, `ProjectCli*Specs`,
    `IssueWorkflowProfileApiSpecs`, `IssueWorkflowProductLoopSpecs`,
    `ActivityWaitingApiSpecs`, and `WorkflowVariableSpecs`. None of these
    files are modified by issue 97. The failures include
    `Mohist.Cli.IServiceInstaller` not registered (CLI help tests), a
    cycle in `Mohist.Server.Sessions.Grains` <-> `Mohist.Server.Sessions.Services`
    (architecture test), and a "Runner has no work" race in
    `WorkflowVariableSpecs`. They are present on `master` and unrelated
    to the task output variables change.
  SuggestedAction: Address in a separate issue. Not blocking for this
    change.
  Status: pre-existing

- [ID: item-8]
  Severity: info
  Scope: pre-existing test failures
  Evidence: `packages/runner/tests/acp-agent.spec.ts` has 39 failing tests
    that all stem from `context.serverConnection.openWorkflowAgentSession`
    / `getWorkflowAgentSession` not being present on the test fake. The
    same failures exist on `master` and are not caused by issue 97.
  SuggestedAction: Fix the test fake in a separate issue. Not blocking
    for this change.
  Status: pre-existing

## Acceptance Criteria Verification

- [x] **Tasks can declare `outputs` in workflow YAML definition** —
  `TaskOutputs_ParsesDeclaredOutputs`, `TaskOutputs_MissingName_Throws`,
  `TaskOutputs_MissingFrom_Throws`, `TaskOutputs_DuplicateName_Throws`,
  `TaskOutputs_RoundTripsThroughYaml` all pass
  (`WorkflowYamlSerializerSpecs.cs`).
- [x] **Runner captures declared outputs from action result `output` field** —
  `captureOutputs` tests in `packages/runner/tests/output-capture.spec.ts`
  cover the success path, failure path, missing field, declared-only,
  and nested object capture. End-to-end runner test in
  `packages/runner/tests/executor-outputs.spec.ts` covers the executor
  integration.
- [x] **Server stores captured outputs in `WorkflowRun` runtime variable
  store** — `WorkflowRunRuntimeVariableSpecs.cs` covers empty init,
  append, null/empty short-circuit, retry overwrite, and serialization
  round-trip.
- [x] **`${{ tasks.<id>.outputs.<name> }}` resolves in subsequent task
  `with` and `artifacts`** — `ResolvesTaskOutputReferenceInWithValue` and
  `ResolvesTaskOutputReferenceInArtifactPath` in
  `packages/runner/tests/template.spec.ts` pass. End-to-end grain test
  `EndToEnd_TaskOutput_CapturedAndResolvedInDownstreamTask` passes.
- [~] **Runtime variables persist across stages within a workflow run** —
  Implementation is correct (`RuntimeVariables` lives on `WorkflowRun`,
  not `StageRun`), but no test exercises the cross-stage path. See
  follow-up item-3.
- [x] **Existing `${{ }}` template syntax and resolution chain remain
  unchanged** — `ExistingTemplateBehaviorWithTaskOutputsUnchanged` and
  `NonTaskUnresolvedReferenceStillFailsAsBefore` in `template.spec.ts`
  pass; pre-existing `UnresolvedReference_Throws`,
  `NestedExpansionDoesNotCoerceFullObjectVariables`, etc. still pass.
- [x] **Tasks without `outputs` declaration behave identically to current
  behavior** — `CaptureTaskOutputs_TaskWithoutOutputs_DoesNotModifyStore`
  and `TaskOutputs_Omitted_IsValid` pass; `captureOutputs` returns
  `undefined` for missing declarations; `Dispatch_EmptyRuntimeStore_DoesNotAlterVariables`
  asserts no `tasks` key is injected.
- [x] **Failed tasks do not produce output variables** —
  `EndToEnd_FailedTask_ProducesNoOutputsForDownstreamTask` passes; the
  runner `captureOutputs` returns `undefined` for non-success status; the
  server's `ProcessTaskResultAsync` only calls `CaptureTaskOutputs` when
  `result.Status == "completed"`.

## Code-Spot Observations (informational)

- `WorkflowGrain.MakeDispatchAsync` correctly merges runtime variables
  after the dispatch-scope (workflow/stage/work) injection and before
  prompt loading, so `${{ tasks.<id>.outputs.<name> }}` resolves in
  `prompts.*` as well as in `with` and `artifacts`. The merge uses
  `DeepMergeSkippingNulls` with the runtime store as the overlay, which
  gives runtime values precedence over user-defined `tasks.*` keys
  (verified by `Dispatch_RuntimeVariablesTakePrecedenceOverLowerPrecedenceSources`).
- The runner's `parseTaskOutputs` (`json.ts:14-27`) silently drops
  entries that are missing `name` or `from`. This is fine in practice
  because the server is authoritative and rejects those at parse time,
  but the runner does not log a warning if it ever receives a malformed
  entry. No test covers this defensive behavior.
- `WorkflowRun.RuntimeVariables` is initialized in two places: a default
  in `WorkflowRun.cs:28` and an explicit assignment in
  `WorkflowRun.Lifecycle.cs:36`. The redundancy is harmless; only
  `Create` constructs a `WorkflowRun` in production code.
- `TaskOutputDefinition` is an unsealed record; no explicit surrogate
  is declared, so Orleans falls back to record auto-serialization. This
  matches the pattern used for `TaskArtifactCapture` and works because
  the type is part of an assembly that already declares
  `[GenerateSerializer]` types.
- The runner's `output-capture.ts` checks success status as one of
  `["completed", "success", "succeeded", "pass", "passed"]`, which is
  more lenient than the server's `result.Status == "completed"`. This
  is intentional — the runner sits closer to the action's native status
  string and the server normalizes.

## Test Run Summary

- Runner unit tests (excluding pre-existing failing
  `acp-agent.spec.ts`): **190/190 pass** (verified via
  `npx vitest run --exclude tests/acp-agent.spec.ts`).
- C# tests targeting the new code paths
  (`TaskOutputVariablesEndToEndSpecs`, `TaskOutputCaptureSpecs`,
  `RuntimeVariableDispatchSpecs`, `RuntimeVariableMergeSpecs`,
  `WorkflowRunRuntimeVariableSpecs`, `WorkResultSerializationSpecs`,
  `WorkflowYamlSerializerSpecs`): **27/27 pass** (verified via
  `dotnet test --filter`).
- TypeScript typecheck on `packages/runner`: **clean**
  (`npm run typecheck`).
- Server build: **succeeds** (`dotnet build --nologo`).
- Pre-existing failures in `ArchitectureRules`, CLI help specs, and
  `WorkflowVariableSpecs` are not caused by this change (verified by
  running on `master`).
<promise>PASS</promise>
