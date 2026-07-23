## Why

The GitHub PR workflow can request check-stage approval for a branch that has not been synchronized with the latest base, and can treat an empty PR-check set as passed after waiting. This leaves approval without evidence that the actual candidate head is current and protected, then defers the resulting failure to integrate.

## What Changes

- Update the built-in `mohist/github-pr` check gate so that, only after AI review passes, it rebases the issue branch onto the latest repository base before publishing the candidate head, marking the PR ready, and verifying its checks.
- Reuse the existing rebase conflict recovery path; a rebase conflict prevents PR check verification until that recovery completes.
- Require the shared PR-check wait used by `mohist/github-pr-checks` and `mohist/merge-github-pr` to treat an empty check set as unavailable after its bounded wait, rather than as a successful verification or merge precondition.
- Preserve integrate's final merge protection and base-moved recovery for changes that occur after check approval.

## Capabilities
- `github-pr-check-gate`: The GitHub PR workflow's post-review gate: synchronize the approved candidate with the current base, publish and verify that same PR head, require non-empty passing checks before approval, and retain existing conflict recovery and final integrate protection.

## Impact

- Built-in GitHub PR workflow definition in `packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-github-pr.workflow.yaml`.
- Runner GitHub PR check polling and classification in `packages/runner/src/actions/github-pr-checks-wait.ts`, plus the `mohist/github-pr-checks` and `mohist/merge-github-pr` callers, with corresponding workflow and runner tests.
- No persistent model, public API, Action input, or dependency changes.
