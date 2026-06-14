## Why

The default workflow's integrate stage currently performs a local squash merge but never pushes the result to the remote repository. After the workspace is cleaned up, the merged commit is effectively lost and the issue reaches "Done" without delivering code to the base branch. This change closes the delivery loop so that a completed integrate stage means the code is on the remote.

## What Changes

- Add a new `mohist/push` workflow action that pushes the current branch to a configured remote.
- Register `mohist/push` in the runner action registry so it can be referenced from workflow definitions.
- Update the built-in `mohist-default.workflow.yaml` to run an `integrate:push` task immediately after `integrate:merge`.
- Push failures (non-fast-forward, auth, branch protection) are treated as terminal task failures: the integrate stage fails and the issue does not advance.

## Capabilities

### New Capabilities

- `git-push-action`: Workflow action that pushes a git branch to a remote. Supports `target` (branch name) and `remote` (default: `origin`) inputs, runs as a pure git operation without an AI agent, and reports push failure as a task failure.

### Modified Capabilities

- `workflow-definition`: The Integrate stage contract now includes `integrate:push` as the final delivery task after `integrate:merge`, before the post-merge health check. Existing requirements about distinct ordered integrate steps and failure handling apply to the new task.

## Impact

- Runner action registry (`packages/runner/src/actions/registry.ts`) gains the `mohist/push` action.
- Default workflow definition (`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml`) is updated.
- Integrate stage execution now depends on successful remote push for workflow completion.
