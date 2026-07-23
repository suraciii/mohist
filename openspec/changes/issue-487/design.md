## Context

The built-in `mohist/github-pr` workflow currently runs `ai-review`, pushes the branch, marks the PR ready, and verifies checks in its check stage. The branch can therefore be behind `repository.baseBranch` when check-stage approval is requested. The existing integrate-stage `base-moved` recovery already rebases, resolves conflicts, pushes, and retries the merge, but that occurs after approval.

The runner's `mohist/github-pr-checks` action polls `gh pr view --json statusCheckRollup`. It waits briefly for an empty rollup, but its empty-entry classification is currently passing, so an empty result becomes successful once the wait expires. Runner reports this observation; the workflow definition controls sequencing and approval. No persistent model, public API, Action input, or dependency changes are permitted.

## Goals / Non-Goals

**Goals:**
- Make check-stage approval evidence apply to an AI-reviewed branch rebased onto the latest configured base and then published.
- Reuse the existing rebase conflict-resolution path without verifying checks while a rebase is unresolved or rerunning a successfully resolved rebase.
- Require at least one completed, non-failing check before `mohist/github-pr-checks` succeeds; return `pr-checks-unavailable` after the existing bounded empty-check wait expires.
- Retain integrate's final merge protection for changes after approval.

**Non-Goals:**
- Add a `mergeable` preflight, new GitHub Action, or new workflow variables.
- Synchronize the branch before AI review passes.
- Remove or weaken integrate-stage base-moved and branch-protection recovery.
- Change repository policy for projects that intentionally report no PR checks.

## Decisions

### Place synchronization directly after successful AI review

Insert a `mohist/rebase` task between `ai-review` and the check-stage `push`, configured with `baseBranch: ${{ repository.baseBranch }}`, `remote: origin`, and `squash: false`. The normal sequence becomes: prepare, AI review, rebase, push, mark PR ready, verify checks. The existing rebase action fetches `origin/<base>` itself and is a no-op for history when the branch already contains that base.

Attach the same conflict handler shape used by integrate: on `error.code=conflict`, run `mohist/opencode` with `prompts.resolve-rebase-conflicts`, with no `retrySelf`. This preserves the in-progress resolved rebase and continues to push rather than restarting it. The rebase task is after `ai-review`, so review failure recovery remains limited to repair and re-review.

Alternative considered: put a rebase in the review recovery handler or before review. Both can modify the candidate that was reviewed, and the latter violates the required ordering. Duplicating rebase behavior in a new action would split Git conflict and cleanup behavior from the established runner implementation.

### Treat an empty check rollup as unavailable, never as passed

Keep polling in `waitForGitHubPrChecks` from the first empty rollup for the configured grace period. If it is still empty at the deadline, return `{ kind: "unavailable" }` with a message that identifies the bounded wait and lack of reported checks. Do this before the normal check classification path. Update empty-entry classification so it cannot independently mean `passed`; only a non-empty set whose entries are all completed without failure may pass.

The action already maps `unavailable` to the actionable `pr-checks-unavailable` error code. The check-stage recovery remains scoped to `pr-checks-failed`, so unavailable evidence blocks the stage instead of invoking a speculative code-fix loop.

Alternative considered: treat repositories with no checks as successful after a delay. That is the current behavior and leaves approval without protection evidence. Adding an Action input to opt out is excluded because the issue requires the built-in GitHub PR gate to be strict and changes no public Action contract.

### Verify the post-rebase published candidate through existing PR identity

Continue using the persisted `vars.github.pr.number`; force-push the rebased `HEAD` before marking the existing PR ready, then query that PR's checks. No SHA variable or extra GitHub query is introduced: ordered task execution makes the check action observe the PR head resulting from the immediately preceding push.

Alternative considered: introduce an explicit head-SHA input and compare it with GitHub before every poll. This adds Action surface and data plumbing without a stated requirement; the workflow's serialized task sequence already establishes the required candidate handoff.

### Preserve integrate as the final authority on mergeability

Leave the integrate `merge-pr` recovery unchanged. A base movement or branch-protection change after check approval remains possible, so its `base-moved`, `pr-checks-failed`, and `protection-conflict` handling is still required.

Alternative considered: remove the integrate rebase once check is synchronized. That would turn the unavoidable post-approval race into a terminal merge failure.

## Risks / Trade-offs

- [A repository emits no checks] -> The workflow now stops with `pr-checks-unavailable`; repository owners must add a check or use a workflow profile whose policy permits no checks.
- [A force-push delays GitHub check registration] -> Retain the existing bounded grace-period polling before reporting unavailable.
- [Rebase conflict requires agent resolution] -> Reuse the existing resolver and do not start publishing or check polling until it completes.
- [Base moves after check approval] -> Keep integrate's serialized final merge and its existing base-moved recovery.
- [Workflow YAML task ordering regresses] -> Add server workflow-profile assertions for rebase placement, its conflict recovery, and the resulting task order; add runner unit tests for empty, pending, failing, and passing rollups using injected timing and fake `gh` responses.

## Migration Plan

1. Update the built-in GitHub PR workflow definition and its profile specification tests.
2. Update runner check classification/polling and runner tests without changing action schemas.
3. Run server and runner typecheck/test suites, then deploy through the normal server and runner release path. Existing workflow runs retain their resolved definition; new runs use the updated built-in profile.
4. Roll back by redeploying the prior server/runner versions. No data migration or cleanup is required. A run blocked after rollout remains resumable according to its persisted workflow definition and can be handled through normal recovery.

## Open Questions

- None. The existing empty-check grace period and unavailable retry limits remain the bounded-wait policy for this change.
