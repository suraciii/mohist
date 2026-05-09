## Context

The current merge implementation has two entry points: `WorktreeManager.mergeBack()` for the merge queue path and `WorktreeManager.mergeApprovedCandidate()` for the integrate-stage path. Both are built around fast-forward semantics: they attempt `git merge --ff-only`, optionally rebase the issue branch, and then fast-forward the base branch. Successful integrate-stage results expose `fastForward` and `rebased`, and downstream workflow output, logs, checkpoints, and tests assert those fields.

This change makes the issue the unit of history on the base branch. The issue branch can keep its detailed task-level commits, but the base branch must receive one generated squash commit per completed issue. The implementation should remove fast-forward as a merge outcome rather than adding a configurable strategy switch.

## Goals / Non-Goals

**Goals:**

- Make every successful issue merge land as one squash commit on the configured base branch.
- Keep merge behavior centralized in `WorktreeManager` so workflow and queue callers do not need to know the git command sequence.
- Generate deterministic commit messages from issue metadata and `tasks.json` when available.
- Remove `fastForward` from successful merge result contracts and downstream output.
- Preserve existing failure reporting shape for merge failures, including target branch, base SHA, candidate head SHA, conflict files, and error message.
- Update tests so squash merge is the only expected success path.

**Non-Goals:**

- No user-facing merge strategy configuration.
- No compatibility path that preserves fast-forward merges.
- No GitHub-style merge method selection between merge commit, squash, and rebase.
- No deletion of feature branch history as part of this change.
- No redesign of worktree status or manual rebase UI; existing fast-forward readiness indicators may remain for readiness/rebase workflows unless they directly describe the final merge result.

## Decisions

### D1: Replace final fast-forward with explicit squash merge

`mergeBack()` and `mergeApprovedCandidate()` will checkout the base branch and run an explicit squash sequence for the issue branch: `git merge --squash <branch>` followed by `git commit` with the generated message. The success path records `landedSha` as the new base-branch commit SHA after the squash commit.

For `mergeApprovedCandidate()`, keep the existing clean-rebase-before-final-merge behavior for non-fast-forward candidates so conflicts are still detected before changing the base branch. After the branch is mergeable, the final operation is still squash commit rather than `--ff-only`. For already fast-forwardable candidates, skip the fast-forward optimization and squash directly.

**Alternatives considered:** Keeping `git merge --ff-only` when possible and squash only after rebase was rejected because it preserves the noisy history for the common clean path. Using `git merge --squash` without preserving the current rebase/conflict checks was rejected because it would move more conflict handling into the base branch working tree and make failure recovery riskier.

### D2: Pass merge metadata as an options object

Change merge entry points to accept issue-level metadata needed for commit-message generation, preferably through an options object rather than adding more positional parameters. The object should include `issueTitle` and optionally parsed `tasks` or a preformatted task summary. `baseBranch` can remain positional for minimal churn or move into the same options object if the implementation updates all call sites together.

Callers already have the required context: integrate-stage has `ctx.issue.title` and `ctx.artifactManager.readTasks(ctx.issue.number)`, while merge queue has the issue record and can use the artifact manager if available or provide a fallback message. `WorktreeManager` should own the final message formatting helper so both merge entry points produce the same mainline commit style.

**Alternatives considered:** Having `WorktreeManager` instantiate or depend directly on `ChangeArtifactsManager` was rejected because it would couple low-level git operations to project artifact storage. Having each caller build raw commit messages was rejected because it duplicates formatting rules and risks inconsistent mainline history.

### D3: Use a deterministic issue-level commit message format

The squash commit subject should be derived from the issue title, with the issue number included for traceability. The body should summarize completed tasks from `tasks.json` when available, using stable ordering from the task file. A practical format is:

```text
<issue title>

Issue: #<number>

Tasks:
- [x] T-001 <title>
- [x] T-002 <title>
```

If `tasks.json` is missing, malformed, or contains no tasks, fall back to a concise body with only the issue number. The merge must not fail solely because task metadata is unavailable.

**Alternatives considered:** Reusing the original branch commit messages was rejected because it keeps the noise this change is meant to remove. Making missing `tasks.json` a merge blocker was rejected because artifact validation belongs to earlier workflow gates and would make merge recovery harder.

### D4: Remove `fastForward` from successful merge results

`MergeTruth` and the `StageContext.WorktreeManager.mergeApprovedCandidate()` return type should remove `fastForward`. Integrate-stage output, task result output, event summaries, logs, and tests should stop reporting it. Do not add a replacement strategy field because there is only one supported strategy; `landedSha` identifies the successful squash result.

The optional `rebased` flag may remain as diagnostic metadata for pre-squash preparation because removing it is outside this issue's scope. It must not imply a different final merge strategy, and success summaries should not present it as the merge method.

**Alternatives considered:** Keeping `fastForward: false` on every result was rejected because it preserves a dead concept in the public contract and invites downstream branching on an impossible outcome. Adding a generic multi-strategy field was rejected because this change intentionally does not introduce multiple strategies. Removing `rebased` was considered, but deferred because the requirement only removes fast-forward status and `rebased` can still help diagnose pre-squash preparation.

### D5: Keep squash failure handling atomic enough for retry

Before attempting the squash, ensure the base branch is checked out and no stale merge/rebase state will interfere. If `git merge --squash` fails, abort or reset the partial squash state where possible before returning an `IntegrationFailure` or failed `mergeBack()` result. If `git commit` fails after staging the squash, return a clear error that the base branch may need cleanup.

The implementation should continue to avoid automatic worktree cleanup on merge failure so the user or agent can inspect and retry. Successful post-merge cleanup/archive behavior remains owned by existing finalization paths.

**Alternatives considered:** Letting git leave partial squash state for all failures was rejected because it creates hidden retry hazards. Wrapping the entire merge in a temporary branch was considered safer but is unnecessary for this scoped change and would add operational complexity.

## Risks / Trade-offs

- [Bisect granularity becomes coarser] → Keep issue branch history intact so task-level commits remain available when deeper debugging is needed.
- [Commit-message generation may miss task details when artifacts are unavailable] → Treat tasks as best-effort metadata and always include the issue number/title fallback.
- [Partial squash state can block later retries] → Abort or reset failed squash attempts where possible and return cleanup-oriented error messages when automatic cleanup cannot be guaranteed.
- [Existing tests and consumers may assert `fastForward`] → Remove the field at the type boundary and update all workflow, queue, and regression tests in the same change.
- [Rebase terminology may confuse users if retained] → Only surface rebase as an internal/pre-squash preparation detail if still needed; success summaries should say squash merge completed.

## Migration Plan

1. Add shared merge metadata and successful merge result types in `worktree-manager.ts` or adjacent type definitions.
2. Implement a shared commit-message formatter from issue number, issue title, and optional tasks.
3. Replace `mergeBack()` final fast-forward logic with checkout, squash merge, commit, and landed SHA lookup.
4. Replace `mergeApprovedCandidate()` fast-forward success and rebase-plus-fast-forward success paths with the same squash finalization helper.
5. Update `stage-context.ts` and all call sites to pass issue metadata and stop reading `fastForward`.
6. Update integrate-stage summaries, logs, event payloads, and task result output to describe squash completion.
7. Update tests to assert `git merge --squash`, generated commit messages, one landed commit, and absence of `fastForward`; remove fast-forward-only cases.
8. Run the package test suite and build.

Rollback is a code rollback to the previous fast-forward implementation. Existing squash commits on the base branch remain valid git history and do not require data migration.
