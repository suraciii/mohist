## Why

The workflow profiles' recovery configuration has design defects that make integrate-stage recovery self-defeating: the rebase conflict handler's `retrySelf` destroys the conflict-resolution agent's work and loops to budget exhaustion; the post-rebase recovery push uses `--force-with-lease` on a tracking-ref-less dynamic branch, which always fails and is misclassified as `base-moved`, triggering a needless rebase; a dead `conflictMode` config lingers; and `archive-change` names its archive directory with a date prefix, so a cross-day retry cannot find the already-archived source and fails permanently. Users must manually clean workspaces to recover.

## What Changes

- Remove `retrySelf: true` from the rebase conflict handler in `mohist-github-pr.workflow.yaml`. The `recover:resolve-rebase-conflicts` agent already finishes the rebase (`git rebase --continue`); after it completes, the flow naturally continues to `recover:push`. `retrySelf` caused `recover:rebase` to re-run, whose `abortRebaseIfInProgress` destroyed the agent's resolved rebase, re-hit the same conflict, and looped until budget exhaustion.
- Remove the dead `conflictMode: task` from the rebase `with` block in `mohist-github-pr.workflow.yaml`. The rebase action never reads this field.
- Change the post-rebase recovery push (`recover:push` under `base-moved`) from `forceWithLease: true` to `force: true`. Dynamic branches (`mohist/run-<runId>`) are single-owner and carry no remote-tracking ref, so bare `--force-with-lease` always fails; `--force` is safe and bypasses the tracking-ref dependency. The check-stage regular push keeps `forceWithLease: true` (no rebase rewriting there).
- Add `force: true` input mode to the `mohist/push` action (`--force`), coexisting with the existing `forceWithLease: true` mode.
- Make `archive-change` idempotent across retries/reruns: before moving the change directory, the action persists the computed archive directory name to a workflow runtime variable; on retry/rerun it reads that variable and reuses the name so `findExistingArchive` locates the already-archived directory. This applies to both `mohist-github-pr` and `mohist/default` profiles.
- Add runner-action support for programmatically writing workflow runtime variables during execution (before task completion), beyond the existing declarative `setVars` that only takes effect after a task succeeds.

## Capabilities

### New Capabilities
- `archive-change-idempotency`: Idempotent `archive-change` across retries and reruns — the action persists the computed archive directory name to a workflow runtime variable before moving the source directory, so a retry (even across a day boundary) reuses the same name and finds the already-archived directory. Backed by runner actions' mid-execution runtime-variable write capability.

### Modified Capabilities
- `pr-first-workflow`: The base-moved recovery requirement changes — the rebase conflict handler no longer retries the rebase (`retrySelf` removed) so the conflict-resolution agent's completed rebase is preserved and the flow continues to `recover:push`; the dead `conflictMode: task` declaration is removed; the post-rebase recovery push uses `force: true` (`--force`) for single-owner dynamic branches instead of `--force-with-lease`, and the `mohist/push` action gains a `force` input mode.

## Impact

- **Server** (`packages/server`): workflow profile YAML `mohist-github-pr.workflow.yaml` — remove `retrySelf` from the rebase conflict handler, remove `conflictMode: task`, switch `recover:push` to `force: true`. `mohist-default.workflow.yaml` has no equivalent recovery config to sync.
- **Runner** (`packages/runner`): `actions/push.ts` gains `force` input support (`--force`); `actions/openspec.ts` (`archiveChangeAction`) persists the archive directory name to a runtime variable before the directory move and reads it on retry; executor/action infrastructure enables mid-execution runtime-variable writes (reusing the existing `patchRunVars` server call path).
- **Tests**: workflow profile assertions for the corrected recovery handlers; push action `force` mode; archive-change idempotency across simulated cross-day retries; runner mid-execution variable write.
- No breaking changes to external APIs; all changes are internal recovery/action behavior fixes.
