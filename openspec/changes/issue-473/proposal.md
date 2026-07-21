## Why

Operator-triggered rebase (`mo issue rebase`) builds an ad-hoc task whose `with` payload carries a `repository` object that the target `mohist/rebase` Action has never declared as an input. The rebase Action historically ignored unused keys, but issue 444's dispatch-time input validation now rejects unknown inputs as `invalid-input` before the Action body runs, so every rebase task fails immediately at validation and the run cannot recover via retry. The strict validation is the correct contract; the fix is to stop sending the field.

## What Changes

- The rebase task's `with` payload no longer includes a `repository` field; it carries only inputs the `mohist/rebase` Action declares today (`baseBranch`, `remote`).
- The run-owned repository context remains in use at the rebase entry point for its two existing responsibilities: a run without repository context is still rejected before the task is queued, and an omitted base branch still defaults from the run's repository snapshot.
- The unit test that today asserts the `repository` field is present on the rebase task's `with` is updated to assert the payload carries only the rebase Action's declared inputs (and no `repository` key).
- No change to the `mohist/rebase` Action manifest, inputs, or implementation; no change to the existing `recover:resolve-rebase-conflicts` recovery task; no change to profile template variables such as `${{ repository.baseBranch }}`, which are resolved by the variable renderer on a separate path and are not Action inputs.

## Capabilities

- `workflow-run-rebase`: Operator-initiated rebase of a workflow run's branch — server-side construction of the ad-hoc rebase task, the inputs handed to the `mohist/rebase` Action (must conform to the Action's declared inputs), run-owned repository context defaulting and missing-context rejection, and unchanged conflict-recovery behavior.

## Impact

- **Server Issue API helpers** (`packages/server/src/Mohist.Server/Api/IssueRoutes.Helpers.cs`): `BuildRebaseTaskWith` stops emitting the `repository` object; the run-owned `WorkflowRepositoryContext` parameter remains in use only for base-branch defaulting.
- **Server Issue API rebase route** (`packages/server/src/Mohist.Server/Api/IssueRoutes.Rebase.cs`): the missing-context rejection and base-branch defaulting logic is unchanged; only the field mirrored into the Action's `with` is removed.
- **Server unit tests** (`packages/server/tests/Mohist.Server.UnitTests/Api/IssueRebaseRecoveryTests.cs`): `BuildRebaseTaskWith_UsesResolvedRepositoryContext` is updated to encode the corrected contract; the sibling recovery test is untouched.
- **No change** to the `mohist/rebase` Action (`packages/runner/src/actions/built-ins.ts`) or to Action input validation (`packages/runner/src/actions/input-validation.ts`, introduced by issue 444) — the manifest remains the authority and the caller now conforms.
- **No change** to CLI surface, persisted models, database schema, recovery task shape, or any other server-constructed task; no new external dependency.
