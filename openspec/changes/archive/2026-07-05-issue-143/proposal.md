## Why

The `mohist/local` plan stage still runs four sequential `core/artifact-exists` checks — `proposal-complete`, `specs-complete`, `design-complete`, `tasks-valid` — that each verify a single openspec artifact file (`mohist-local.workflow.yaml:91-110`). The `mohist/github-pr` profile already collapsed these into one `mohist/openspec-artifacts` check (issue-270), so `local` is now the inconsistent outlier. The four dispatches are redundant with the plan tasks' own `expect.files` declarations, inflate the issue timeline with near-identical rows, add latency, and give the user no signal the task expects did not already provide. We finish the consolidation now so both built-in profiles gate plan artifacts the same way, while keeping the failure message actionable.

## What Changes

- Replace the four `core/artifact-exists` checks in `mohist-local.workflow.yaml` (`proposal-complete`, `specs-complete`, `design-complete`, `tasks-valid`) with a single `plan-artifacts` check that `uses: mohist/openspec-artifacts` with `changeDir: ${{ openspecChangeDir }}`, mirroring `mohist-github-pr.workflow.yaml:127-132`.
- Extend `openspecArtifactsAction` (`packages/runner/src/actions/openspec.ts:123`) to also verify the `specs/` directory, so the consolidated check covers all four plan artifacts (`proposal.md`, `specs/`, `design.md`, `tasks.json`). The current "specs is optional" behavior (`openspec.spec.ts:1413`) is retired — `local`'s existing `specs-complete` gate must be preserved by the consolidated check.
- On failure, the check message names every missing artifact (path list), so the single row stays as actionable as the four it replaces.
- `self-review-passed` (quality gate) and `health` (formatting gate) checks are unchanged, as are the check, build, and integrate stages.
- Update affected tests: the runner `openspec-artifacts` spec (specs now required, not optional) and the server local-profile workflow spec (assert a single `plan-artifacts` check and absence of the four old names), plus the `VariableScopeSpecs.cs` fixture that still models the four `core/artifact-exists` checks.

## Capabilities

- `plan-artifact-gate`: How the plan stage gates on openspec artifacts through one consolidated `mohist/openspec-artifacts` check — the required artifact set (`proposal.md`, `specs/`, `design.md`, `tasks.json` under the change dir), failure reporting each missing artifact by path, and the replacement of the per-artifact `core/artifact-exists` dispatches. `self-review-passed` and `health` remain separate concerns outside this capability.

## Impact

- **Runner (TypeScript)**: `packages/runner/src/actions/openspec.ts` — `openspecArtifactsAction` adds the `specs/` directory to its required set; failure `output.missing` and message now include `specs/` when absent. This also tightens the `mohist/github-pr` profile, which already uses this action and already produces `specs/` in its plan stage.
- **Server (workflow YAML)**: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-local.workflow.yaml` — plan `checks` reduced from six entries to three (`plan-artifacts`, `self-review-passed`, `health`).
- **Runner tests**: `packages/runner/tests/openspec.spec.ts` — the "specs is optional" case flips to a required-artifact failure case; the all-present case already creates `specs/`.
- **Server tests**: a local-profile counterpart of `GithubPrWorkflowDefinition_PlanStage_HasSingleOpenspecArtifactsCheck` (`MohistPrIssueWorkflowProfileSpecs.cs:253`); `VariableScopeSpecs.cs:251-254` fixture updated to the single `mohist/openspec-artifacts` check.
- **Docs**: `design/workflow/builtin-workflows/local.md` and `docs/workflow-profiles.md` (the `proposal-complete`/`specs-complete`/`design-complete`/`tasks-valid` example block) updated to the consolidated check.
- **No API, storage, dependency, or task `expect.files` changes**; non-breaking at the workflow-profile-id level.
