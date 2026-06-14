## Context

The default workflow's `integrate` stage currently ends with `mohist/merge`, which performs a local squash merge of the issue branch into the base branch and commits it inside the isolated workspace. The workspace origin already points to the real upstream remote, but no action sends the new commit there. After the workspace is cleaned up, the merged code can be lost while the issue still reaches a terminal "Done" state.

The runner action registry has no push action, and the built-in workflow profile has no push task. The `integrate` stage already uses `lockBehavior: sequential` and the `project-integration` resource, so it is the right place to add a non-concurrent remote push.

## Goals / Non-Goals

**Goals:**
- Add a `mohist/push` workflow action that pushes a git branch to a remote as a pure git operation.
- Register `mohist/push` in the runner action registry.
- Update the built-in `mohist-default.workflow.yaml` to run `integrate:push` immediately after `integrate:merge` and before `health:integrate`.
- Treat push rejection (non-fast-forward, auth, branch protection) as a terminal task failure that stops the stage.
- Expose `target` (branch name) and `remote` (default `origin`) inputs.

**Non-Goals:**
- PR/MR-based delivery workflows.
- Automatic rollback after a failed or rejected push.
- Remote feature-branch deletion after push.
- Retry/backoff logic for transient push failures.

## Decisions

**1. Implement `mohist/push` as a local action in the existing registry file.**
- *Rationale:* The action is small, git-only, and semantically adjacent to `mergeAction` and `mergeReadyAction`. Keeping it in `packages/runner/src/actions/registry.ts` matches the current layout and avoids a proliferation of single-function modules.
- *Alternative considered:* A separate `packages/runner/src/actions/push.ts` file. Rejected because the existing registry already inlines the other git actions and the new action is not large enough to justify a new module.

**2. Reuse the module-level `git` helper and the existing `runCommand` infrastructure.**
- *Rationale:* `git.ts` already wraps `runCommand` with `success`/`combinedOutput` helpers. Reusing it gives consistent error capture and testability (the existing `setMergeGitRunnerForTest` setter actually controls the shared `git` runner for all git actions).
- *Alternative considered:* Calling `runCommand("git", ...)` directly. Rejected because it would duplicate combined-output formatting and bypass the test seam.

**3. Make `target` default to the current branch and `remote` default to `origin`.**
- *Rationale:* After `mohist/merge` checks out the base branch, HEAD is already the base branch, so defaulting `target` to HEAD keeps the action usable with no inputs while still allowing explicit configuration.
- *Alternative considered:* Requiring `target: ${{ project.baseBranch }}` in the workflow. Rejected because it duplicates merge configuration and creates a mismatch risk; the default workflow will still pass `target` explicitly for clarity.

**4. Push the resolved refspec as `git push <remote> <branch>` and capture all git output on failure.**
- *Rationale:* An explicit refspec avoids depending on `push.default` configuration and makes the action predictable in shared workspaces. Returning the full `combinedOutput` in the failure message and output preserves the remote rejection reason for debugging.

**5. Update only the built-in workflow profile.**
- *Rationale:* The product shape specifically targets the default workflow. Custom profiles can opt into `mohist/push` once it is registered.

## Risks / Trade-offs

- `[Risk]` If the runner environment lacks credentials for the upstream remote, push fails and the issue remains in integrate. -> Mitigation: Credential setup is an operational prerequisite; the failure message surfaces the exact git error so operators can diagnose auth issues.
- `[Risk]` A non-fast-forward rejection leaves the workspace with a local commit that is ahead of the remote. -> Mitigation: The sequential `project-integration` lock prevents concurrent pushes from other Mohist issues; if an external actor pushes in between, the failure is terminal and the issue must be re-integrated.
- `[Risk]` Push failure is currently non-retryable, so transient network errors may fail the stage. -> Mitigation: This is an explicit non-goal; retries can be added later without changing the action contract.
- `[Risk]` Tests that mock `git` via `setMergeGitRunnerForTest` affect all git actions, including push tests. -> Mitigation: Tests must reset the runner between cases, which is already the pattern for merge action tests.

## Migration Plan

1. Implement `pushAction` in `packages/runner/src/actions/registry.ts` and register it as `mohist/push` in `createDefaultRegistry()`.
2. Add `integrate:push` to `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml` after `integrate:merge` and before `health:integrate`.
3. Add unit tests for the push action using the shared git runner test seam.
4. Deploy the runner/server; no database migration is required.
5. Rollback: revert the registry change and the workflow YAML edit. In-progress runs that have already executed `integrate:merge` without push will not retroactively push; new runs will follow the updated workflow.

## Open Questions

- Should `integrate:push` explicitly pass `target: ${{ project.baseBranch }}`, or rely on the HEAD default? Passing it makes the workflow more self-documenting.
- Should the push action verify the remote ref after a successful push and return the new remote commit SHA in the output? This would make "code is on the remote" easier to confirm in `mo issue show`.
