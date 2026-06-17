## Why

Issue #121 exposed that Mohist can report a local runtime as healthy while the runner is still executing stale ignored `dist` output instead of code matching the current source. The same failure path also showed that the default Integrate workflow still has duplicate push ownership, increasing the risk of repeated delivery actions and confusing recovery when merge behavior changes.

## What Changes

- Ensure full `mo update` refreshes runner build output and restarts the runner when the runner is installed and manageable.
- Make skipped runner update paths explicit in command output and verification results, including cases where the runner is not installed or not in scope.
- Extend update verification beyond service availability so stale runner `dist` or live-code identity mismatches are detected instead of passing as merely active/connected.
- Clarify `mo update server` semantics so users are not led to believe runner runtime code was refreshed by a server-only update.
- Remove or constrain the default Integrate workflow's independent `integrate:push` step when `mohist/merge` owns pushing through `push: true`.
- Add regression coverage for runner update consistency and single push ownership in the default Integrate path.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `cli-interface`: Add user-visible `mo update` and `mo update server` behavior requirements for runner build/restart coverage, skipped-runner messaging, and stale runner runtime verification.
- `workflow-definition`: Change the built-in default Integrate workflow contract so push has a single owner when `mohist/merge` is configured with `push: true`.

## Impact

- CLI update command behavior and output, including full update, server-only update, and post-update verification.
- Runner build artifacts under `packages/runner/dist` and the managed runner service restart path.
- Runner runtime identity checks used to determine whether the live runner matches the current source/build output.
- Built-in default workflow configuration for Integrate delivery tasks.
- Regression tests around local update consistency and Integrate push ownership.
