## Context

Today, Mohist workflow tasks have no guarantee that the git worktree is clean when a task reports success. The `mohist/merge` action creates a local squash merge commit on whatever branch is checked out, without verifying worktree cleanliness, fetching the latest remote target, or pushing the result. This caused issue #102 to complete `integrate:merge` with 29 unstaged modified files still in the worktree — the real failure (a prior task leaving uncommitted changes) was invisible until a later push step crashed.

The proposal and specs define two new capabilities (`task-cleanup`, `merge-delivery`) and modify six existing specs to close these gaps.

## Goals / Non-Goals

**Goals:**
- Enforce clean `git status --porcelain` before any task can be marked completed.
- Give agent-backed tasks a bounded cleanup loop (re-prompt same session) when the worktree is dirty.
- Produce structured dirty-worktree evidence so failures are diagnosable.
- Rewrite `mohist/merge` to own the full delivery: fetch remote target → rebase source → squash land on remote target HEAD → fast-forward push.
- Make push part of the merge action contract (`push: true`), not a separate workflow task.
- Add a merge boundary guard that rejects dirty source worktrees before any delivery operations.
- Implement remote-advanced retry with bounded attempts.
- Classify merge failures by phase (`source-cleanup`, `fetch`, `rebase-conflict`, `landing-validation`, `push`).

**Non-Goals:**
- No read-only task types or broad task classification system.
- No separate `mohist/publish` action or standalone `integrate:push` task.
- No auto-commit, stash, discard, or force push from the merge action.
- No workflow stage checks used for merge action internal validation.
- No redesign of the workflow YAML schema.

## Decisions

### 1. Clean worktree check lives in `WorkExecutor.executeOne()`, not in individual actions

The post-action `git status --porcelain` check is added to the executor's `executeOne()` method, which is the single choke point where all task results pass through before being reported to the server. After the action handler returns successfully, the executor checks worktree cleanliness. If clean, the task completes normally. If dirty, the executor either enters the cleanup loop (agent-backed tasks) or fails immediately with dirty-worktree evidence (deterministic actions).

**Alternatives considered:**
- *Each action handles it internally.* Rejected: every action author must remember the check; violates DRY.
- *Check at the `executeAndReport()` level in `RunnerHost`.* Rejected: `executeAndReport` doesn't know workDir — the executor already resolves it. The check belongs at the same level that runs the action.

### 2. Agent cleanup loop is executor-side, not inside `acpAgentAction`

When an agent-backed task returns with dirty worktree, the executor builds a constrained cleanup prompt and calls the same `acpAgentAction` again with the same session reference. The executor loops up to `maxCleanupAttempts` (default 3), re-checking `git status --porcelain` after each attempt.

Agent-backed tasks are identified by checking `work.uses` against a known set (`mohist/acp-agent`). Deterministic actions that leave dirty worktree fail immediately without a cleanup loop — the action itself is responsible for leaving a clean worktree.

The cleanup prompt explicitly tells the agent: (a) do not start new work, (b) do not push, (c) inspect uncommitted changes and either commit task-related ones or revert unrelated ones, (d) report a summary and commit SHA or no-change result.

**Alternatives considered:**
- *Build cleanup into `acpAgentAction`.* Rejected: the ACP agent action is a generic agent runner; it should not know about git worktree state.
- *Create a separate cleanup action.* Rejected: adds indirection; the cleanup must use the same session as the original task, which the executor already has access to via the session manager.

### 3. Merge action: complete fetch→rebase→land→push pipeline

The new `mergeAction` replaces the old local-merge approach with a sequenced pipeline:

```
guard (git status --porcelain)
  → fetch (<remote> <target>)
  → rebase (source onto <remote>/<target>)
    → [on conflict] resolve via agent (same `mergeConflictResolverRunner` pattern)
  → land (checkout <remote-target-sha> as detached HEAD → git merge --squash <rebased-source> → git commit)
  → validate-landing (clean worktree, correct parent, no in-progress merge/rebase)
  → push (git push <remote> <landing-sha>:refs/heads/<target>, fast-forward only)
  → verify (git ls-remote to confirm remote ref)
  → [on remote-advanced] retry from fetch (bounded)
```

Each phase produces structured failure evidence on error, with a `phase` field identifying where the failure occurred.

**Alternatives considered:**
- *Create a temporary branch for landing.* Rejected: detached HEAD is simpler and prevents accidental branch pollution. The landing commit is identified by SHA, not branch name.
- *Use `git commit-tree` for landing.* Rejected: over-engineered for a single commit. The `checkout → merge --squash → commit` flow is well-understood and debuggable.
- *Use `git merge --ff-only` after squash.* Rejected: squash merge by definition produces a new commit, so fast-forward from the source branch is not applicable. The fast-forward property applies to the push (remote target must move from fetched base to landing commit).

### 4. Push uses explicit commit-to-ref syntax

The push command is:

```
git push <remote> <landingSha>:refs/heads/<target>
```

This pushes the exact landing commit to the remote target ref. Git will reject the push if the landing commit's parent is not the current tip of the remote target (i.e., it's not a fast-forward). No `--force` or `--force-with-lease` is used. If rejected, the remote-advanced retry loop triggers.

### 5. spec-sync: commit after deterministic copy, cleanup loop for agent path

The `openspecSyncAction` currently does a file copy (`copyDirectory`) and returns success. After this change, it commits the copied files:

```
git add specs/ && git commit -m "Sync OpenSpec specs from change delta"
```

If there are no changes to commit (specs already synced), it returns success with a no-change marker. The executor's post-action clean worktree check then verifies the commit succeeded.

For agent-backed spec-sync workflows (intelligent sync per REQ-WFE-005), the agent cleanup loop handles dirty worktree. The commit-or-clean logic is the same invariant regardless of whether the sync is deterministic or agent-driven.

### 6. Structured evidence shapes

Two new structured output objects:

**Dirty-worktree evidence** (`kind: "dirty-worktree"`):
```json
{
  "kind": "dirty-worktree",
  "staged": ["file1.ts", "file2.ts"],
  "unstaged": ["file3.ts"],
  "untracked": ["newfile.ts"],
  "cleanupAttempts": 3
}
```
Populated from `git diff --cached --name-only`, `git diff --name-only`, and `git ls-files --others --exclude-standard`.

**Merge phase failure** (`kind: "merge"`):
```json
{
  "kind": "merge",
  "phase": "push",
  "source": "mo/issue-112",
  "target": "master",
  "strategy": "squash",
  "landingSha": null,
  "pushRetryAttempts": 3,
  "lastRemoteSha": "abc123",
  "output": "..."
}
```
Phase is one of `source-cleanup`, `fetch`, `rebase-conflict`, `landing-validation`, or `push`.

These go in the `output` field of `ActionResult` / `WorkItemResult`. The WorkflowRun persists them as task output JSON.

### 7. No changes to checks

Checks are read-only validators (REQ-WFE-001). The clean worktree check is a task-level invariant, not a check. The merge boundary guard (`source-cleanup`) is an action-internal validation, not a workflow check. Checks remain untouched.

### 8. Conflict resolution stays with existing `mergeConflictResolverRunner` pattern

The rebase conflict resolution reuses the same `mergeConflictResolverRunner` (ACP agent) already used for merge conflicts. The only difference is that rebase conflicts are resolved before the landing step, rather than after a failed merge. The resolver receives the list of conflicted files and resolution rules. After resolution, the rebase is continued (`git rebase --continue`). The same `maxConflictRetries` bound (default 3) applies.

## Risks / Trade-offs

- **[Risk] Merge action becomes long-running.** The full pipeline from fetch through push with retries could take minutes. If the runner process crashes mid-flow, the merge may be only partially complete (e.g., rebase done but landing not committed). → **Mitigation:** The merge action is idempotent-ish — if retried, it starts fresh from fetch. The existing `reportDrain` pattern in `RunnerHost` provides a best-effort report on SIGINT. No new persistent state is created mid-action that would prevent re-execution.

- **[Risk] Cleanup loop abuse.** An agent could deliberately leave changes to get more compute time. → **Mitigation:** Bounded attempts (default 3). The cleanup prompt explicitly constrains behavior (no new work, no push). After exhaustion, the task fails.

- **[Risk] Remote-advanced loop could thrash.** If many issues are merging to the same target concurrently, the retry loop could burn attempts without success. → **Mitigation:** The merge stage uses a `sequential` lock with `project-integration` resource, serializing merges. Bounded push retry (default 5). Exhaustion produces structured evidence.

- **[Risk] spec-sync auto-commit may capture unintended files.** If other files are dirty in the worktree when spec-sync runs, the commit will include them. → **Mitigation:** The `openspecSyncAction` commits only the `specs/` directory (`git add specs/`). If the worktree is dirty from prior tasks, the merge boundary guard catches it before merge runs.

- **[Trade-off] Clean worktree check adds ~100ms overhead per task.** Each task completion now runs `git status --porcelain`. This is negligible compared to task execution time (seconds to minutes).

## Migration Plan

1. **Implement new code paths alongside old ones.** The new merge action is a rewrite of the existing `mergeAction` function. The old behavior (local merge without push) remains available by omitting `push: true` from the workflow YAML. The clean worktree check is additive — it gates completion, not execution.

2. **Update default workflow YAML.** Add `push: true` and `remote: origin` to the `integrate:merge` task `with` block. This is a single-file change. Existing workflow runs in flight when the runner updates will use the new behavior on their next poll.

3. **No database migration.** WorkflowRun already stores task output as an arbitrary JSON string. The new structured evidence shapes fit in the existing `output` field. No schema changes needed.

4. **Test update strategy.** Existing merge tests (`packages/runner/tests/merge.spec.ts`) use `setMergeGitRunnerForTest` to inject mock git implementations. The new merge action follows the same pattern — the mock git runner receives the new command sequences. Add regression tests reproducing the #102 shape: agent task leaves changes → cleanup requested → merge refused until clean → successful merge+push in one action.

5. **Rollback.** Revert the runner code and the workflow YAML change. Since no persistent state format changes, old runners can immediately pick up work. In-flight merge actions that crash during rollout are safe to retry on the old code (they produce local merge commits only).

## Open Questions

- **Default `maxCleanupAttempts`?** Propose 3. Each cleanup attempt is a full agent round-trip. Three attempts gives a reasonable chance for the agent to fix simple omissions without excessive delay.
- **Default `maxPushRetry`?** Propose 5. In practice, since the Integrate stage has a `project-integration` resource lock, remote-advanced races are unlikely. Five retries covers edge cases like a human pushing to the target branch concurrently.
- **Should the cleanup loop report intermediate results to the server?** The current design only reports the final result (completed or failed), not each cleanup attempt. Intermediate reporting would let users see cleanup progress but adds complexity. Defer to implementation.
- **What happens if `git status --porcelain` itself fails?** The git command could fail if the worktree is corrupted, git is not installed, etc. The checker should treat the git failure as a task failure with structured evidence indicating the check itself failed, distinct from a dirty-worktree result.
