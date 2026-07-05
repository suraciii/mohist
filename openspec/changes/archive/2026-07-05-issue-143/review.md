# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

Acceptance criteria checked against the post-build candidate snapshot:

- `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-local.workflow.yaml:90` now has exactly three plan checks: `plan-artifacts`, `self-review-passed`, and `health`; there is exactly one artifact gate at lines 91-95 using `mohist/openspec-artifacts` with `changeDir: ${{ openspecChangeDir }}`.
- The retired local check names `proposal-complete`, `specs-complete`, `design-complete`, and `tasks-valid` do not appear in `mohist-local.workflow.yaml`; their only non-archive product-code references are negative assertions in `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:285`.
- `self-review-passed` and `health` remain distinct local plan checks at `mohist-local.workflow.yaml:96` and `mohist-local.workflow.yaml:113`; `health` still runs `git diff --check` at line 117.
- `packages/runner/src/actions/openspec.ts:127` requires exactly four artifacts in order: `proposal.md` as a file, `specs` as a directory, `design.md` as a file, and `tasks.json` as a file. The action accumulates every missing path at lines 134-137 and includes the full missing list in the failure message at line 157.
- Runner coverage in `packages/runner/tests/openspec.spec.ts:1375` verifies the all-present success case, `:1413` verifies missing `specs`, `:1467` and `:1485` verify wrong-kind paths, and `:1502` verifies multiple missing artifacts are all reported.
- Server coverage in `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:274` verifies the local profile's consolidated plan-artifacts check and retained `self-review-passed`/`health` checks. `packages/server/tests/Mohist.Server.Tests/Specs/Foundation/VariableScopeSpecs.cs:251` updates the workflow fixture to the same consolidated check shape.
- Documentation was updated where the current behavior is described: `design/workflow/builtin-workflows/local.md:20`, `design/workflow/builtin-workflows/github-pr.md:124`, and `docs/workflow-profiles.md:37`.

Verification run:

- `mo issue show 143 --project-id proj_f6c141d63b6243bfbb481737b2243b87` read the current issue and acceptance criteria.
- `git diff --check master...HEAD` passed with no whitespace errors.
- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 65 files, 910 tests.
- `npm test` passed: root script completed dotnet tests plus workspace test suites; runner summary showed 65 files, 910 tests.
- `dotnet test Mohist.sln --no-restore --filter "FullyQualifiedName~Mohist.Server.Tests.Specs.Issue.Profile|FullyQualifiedName~VariableScopeSpecs"` passed: 164 passed, 6 skipped. The command also emitted existing preview-SDK, npm audit, and Rollup annotation warnings during build; none were failures and none are introduced by this change.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs`
  Evidence: The new local-profile test `LocalWorkflowDefinition_PlanStage_HasSingleOpenspecArtifactsCheck` lives in a class named `MohistGithubPrIssueWorkflowProfileSpecs` at line 274. It is behaviorally correct and green, but it makes the file/class naming less precise as local-profile coverage grows.
  SuggestedAction: Move local-profile-only assertions to a dedicated local workflow profile spec class if more local-profile cases are added.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-local.workflow.yaml`, `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-github-pr.workflow.yaml`
  Evidence: The issue rationale says every plan task already declares `expect.files`, but the `specs` task in both built-in profiles has `artifacts.files` and no task-level `expect.files` (`mohist-local.workflow.yaml:41`, `mohist-github-pr.workflow.yaml:44`). This is not a regression from the candidate because the previous local `specs-complete` stage check also validated `specs` only at stage-check time, and the new consolidated action preserves that gate.
  SuggestedAction: If the product invariant is that every plan artifact is also task-expected, add `expect.files` to the `specs` task in a separate change.
  Status: pre-existing

- [ID: item-3]
  Severity: info
  Scope: `docs/workflow-profiles.md`
  Evidence: The product docs already include implementation-oriented detail such as the workflow YAML file path at line 7 and workflow action names in the GitHub PR example at line 104. The candidate did not introduce those existing details, and its local-profile edit avoids adding the action name there.
  SuggestedAction: Clean up the product/design language boundary for this doc in a documentation-focused pass.
  Status: pre-existing

<promise>PASS</promise>
