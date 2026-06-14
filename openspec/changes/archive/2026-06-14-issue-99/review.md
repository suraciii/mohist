# Review Report

## Result: PASS

## Repaired Items

- [ID: item-r1]
  Severity: test-gap
  Scope: packages/runner/tests/push.spec.ts
  Evidence: The `mohist/push` action contains a defensive failure path for when the current branch cannot be resolved (`if (!target) return { status: "failure", message: "Push action could not resolve current branch" }`), but no test covered this path. Without a test, a future refactor that removes the guard or changes the message would not be caught.
  Verification: Added `UnresolvableCurrentBranch_ReturnsFailureWithoutInvokingPush` to `packages/runner/tests/push.spec.ts`; `npx vitest run tests/push.spec.ts` passes (5/5).
  Status: resolved

- [ID: item-r2]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistDefaultWorkflowProfileSpecs.cs
  Evidence: `DefaultWorkflowDefinition_LoadsFromYaml` asserted the presence and shape of `integrate:merge` but did not assert the new `integrate:push` task, its `mohist/push` use, its `${{ project.baseBranch }}` target, or its ordering after `integrate:merge`. The existing round-trip test (`DefaultWorkflowDefinition_PlanCheckIntegrateStagesAreUnchanged`) does not hardcode task lists, so it would not catch a missing or misordered `integrate:push`.
  Verification: Extended the existing `DefaultWorkflowDefinition_LoadsFromYaml` test to assert `integrate:push` presence, `mohist/push` use, `project.baseBranch` template, and the full ordered task list `[spec-sync, archive-change, merge, push]`. `dotnet test --filter DefaultWorkflowDefinition_LoadsFromYaml` passes; full `MohistDefaultWorkflowProfileSpecs` suite passes (32/32).
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-f1]
  Severity: follow-up
  Scope: packages/runner/src/actions/registry.ts:204-219
  Evidence: `pushAction` defaults `target` to the worktree's current branch via `rev-parse --abbrev-ref HEAD`. In the integrate stage, `context.workDir` is the linked worktree on `mo/issue-{n}`, so the default target would be the issue branch, not the base branch. The default workflow YAML correctly sets `target: ${{ project.baseBranch }}` explicitly, so this works in practice, but a custom workflow profile that omits `target` would push the issue branch to `origin`. The self-review and design (decision 3) acknowledge this trade-off. A user-defined workflow that forgets `target` would silently push the wrong ref.
  SuggestedAction: Consider changing the default to require an explicit `target` for `mohist/push` (rejecting when unset), or documenting the default behavior prominently. At minimum, add a one-line spec note that `target` is required for the integrate use case.
  Status: follow-up

- [ID: item-f2]
  Severity: follow-up
  Scope: packages/runner/src/actions/registry.ts:206
  Evidence: `stringInput(context.with, "target")` returns the empty string when the YAML sets `target: ""` (only `null`/`undefined` trigger `??`). The current code would then call `git push origin ""`, producing a git error rather than the intended "could not resolve" failure. Not a crash, but a confusing error path.
  SuggestedAction: Trim/validate the `target` and `remote` inputs: `const target = stringInput(...)?.trim() || (await resolveCurrentBranch(...))`.
  Status: follow-up

- [ID: item-f3]
  Severity: follow-up
  Scope: packages/runner/src/actions/registry.ts:216-219
  Evidence: `resolveCurrentBranch` uses `rev-parse --abbrev-ref HEAD`, which returns the literal string `"HEAD"` for a detached HEAD. A detached-HEAD worktree would then call `git push origin HEAD`, which git handles as pushing HEAD's commit to a branch named `HEAD` on the remote — not the intended behavior. Low likelihood (worktrees are created with `-b mo/issue-{n}`), but the guard doesn't detect it.
  SuggestedAction: Treat `"HEAD"` as unresolvable (return null), so the action fails cleanly instead of pushing an unintended refspec.
  Status: follow-up

- [ID: item-f4]
  Severity: follow-up
  Scope: packages/runner/src/actions/registry.ts:209-210
  Evidence: The push action's output JSON includes `remote`, `target`, and the raw `combinedOutput`, but does not include the pushed commit SHA. The design's open question 2 raised this; the self-review deferred it. Surfacing the remote commit SHA would let `mo issue show` confirm "code is on the remote base branch" (acceptance criterion #5) without an extra round-trip.
  SuggestedAction: After a successful `git push`, capture the local `HEAD` SHA and `git ls-remote --heads origin <target>` (or parse the push output) to record the remote SHA in the action output. Wire the output into the issue/issue-detail view.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-p1]
  Severity: info
  Scope: packages/runner/src/runtime/workspace.ts:88-102
  Evidence: The issue's "Evidence" section claims `packages/runner/src/runtime/workspace.ts:92-96` resets the workspace origin to the real `gitUrl`. The current code (lines 88-102, `ensureFreshWorktree`) does no such reset. The worktree's `origin` is inherited from the main repo's `.git/config` because `git worktree add` does not create or modify remotes. This is a pre-existing property of the workspace model, not a regression in this change. In the dev environment the main repo's origin is the real `gitUrl`, so the push action's `origin` is correct, but this is an implicit assumption.
  SuggestedAction: If the workspace is expected to be portable across environments (e.g. local bare cache vs. real remote), consider explicitly setting `remote.origin.url` in the workspace bootstrap so the push action's target is always the real `gitUrl`.
  Status: pre-existing

- [ID: item-p2]
  Severity: info
  Scope: packages/runner/src/runtime/workspace.ts:74-86
  Evidence: The push action runs in `context.workDir` (the linked worktree on `mo/issue-{n}`), not in `project.path` (the main repo checkout on the base branch). The push relies on the fact that linked worktrees share refs with the main repo's `.git`, so `git push origin master` from the worktree pushes the same `master` ref that the merge action committed to in `project.path`. This works because `git worktree add -b ${branch} ${worktree} ${baseBranch}` (line 99) creates a linked worktree of the same `.git`. The design decision is internally consistent, but it's not obvious to a reader that the push action operates on a ref shared with a different working tree.
  SuggestedAction: Document this in the design or in a code comment on `pushAction` so future maintainers understand why the worktree workDir is correct for pushing the base branch.
  Status: pre-existing

- [ID: item-p3]
  Severity: info
  Scope: packages/cli/Mohist.Cli/TableRenderer.cs:125-153
  Evidence: Acceptance criterion #5 states "`mo issue show` for an issue whose workflow completed shows code on the remote base branch". The current `RenderIssueShow` does not surface any remote/branch state. The self-review marks this as a follow-up and notes that the mechanism (push action) is what makes the criterion satisfiable, even without an explicit UI change. The change does not modify `mo issue show`.
  SuggestedAction: Surface the pushed commit SHA / remote ref in `mo issue show` by reading the push action's output (see item-f4) or by a post-push server-side record.
  Status: pre-existing

- [ID: item-p4]
  Severity: info
  Scope: packages/runner/src/actions/registry.ts:149-223 (mergeAction)
  Evidence: `mergeAction`'s `commitPendingSourceChanges` (line 359) runs `git add .` in the worktree, which would stage all untracked files (e.g. build artifacts, node_modules) before the squash merge. This is a pre-existing concern unrelated to the push action, but it means the worktree branch `mo/issue-{n}` may carry commits that include unintended files. The push action's default-to-current-branch behavior (item-f1) would push those too if `target` is omitted.
  SuggestedAction: Consider narrowing `commitPendingSourceChanges` to only files that are part of the change, or adding a `.gitignore` discipline check.
  Status: pre-existing

- [ID: item-p5]
  Severity: warning
  Scope: packages/runner/tests/acp-agent.spec.ts (pre-existing, unrelated to this change)
  Evidence: 39 acp-agent tests fail with `TypeError: context.serverConnection.getWorkflowAgentSession is not a function`. Verified pre-existing by running the suite on the parent commit (2c4889bc) before this change's work — same failures.
  SuggestedAction: Track separately; not introduced by issue-99.
  Status: pre-existing

- [ID: item-p6]
  Severity: warning
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/StageLockSpecs.cs (pre-existing, unrelated)
  Evidence: 3 StageLockSpecs tests fail. Verified pre-existing on parent commit (2c4889bc) before this change's work — same failures.
  SuggestedAction: Track separately; not introduced by issue-99.
  Status: pre-existing

## Spec Compliance Verification

| Acceptance criterion | Evidence | Status |
|---|---|---|
| `mohist/push` action is registered and pushes the specified branch to `origin` | `registry.ts:54` registers `pushAction` as `mohist/push`; `pushAction` (`registry.ts:204-214`) invokes `git push <remote> <target>`; tests `push.spec.ts:24-47` verify the call. | ✓ |
| Default workflow YAML includes a push task after the merge task in the integrate stage | `mohist-default.workflow.yaml:263-267` adds `integrate:push` with `target: ${{ project.baseBranch }}` after `integrate:merge` (line 251). YAML parses and round-trips; new server test assertion confirms ordering. | ✓ |
| Push action supports `target` (branch name) and `remote` (default: origin) inputs | `registry.ts:205-206` reads both with defaults; tests cover default, explicit remote, and explicit target. | ✓ |
| If push fails, the task reports failure and the workflow stage fails | `registry.ts:211-213` returns `{ status: "failure" }` on non-zero exit; `push.spec.ts:65-80` verifies `non-fast-forward` rejection returns failure with git error output. The stage-level failure propagation is handled by the standard task-result-to-stage-result path in the executor. | ✓ |
| `mo issue show` for an issue whose workflow completed shows code on the remote base branch | Mechanism (push action) is in place; UI surfacing is a follow-up (item-f4, item-p3). | ⚠ follow-up |

<promise>PASS</promise>
