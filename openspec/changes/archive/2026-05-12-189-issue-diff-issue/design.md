## Context

Mohist issue review currently uses `git diff <base>...<head>` for issue-level file diffs in both the HTTP API and the `mo issue diff` CLI command. That behavior is correct for "what changed since branch point" but wrong for Mohist's intended review surface, because issue branches regularly merge the base branch and therefore carry merged base-branch changes in their history.

The existing review UI already treats Files changed as a primary approval surface and expects the API to provide complete per-file diff content plus `--numstat` summaries. The problem is therefore not in rendering, storage, or commit inspection; it is specifically in how the issue-level diff range is computed.

## Goals / Non-Goals

**Goals:**

- Make issue-level diff review compare the current base branch worktree to the issue branch worktree, so only issue-owned changes are shown.
- Keep API and CLI diff behavior consistent by using the same git comparison semantics in both places.
- Keep existing diff response shape, availability handling, file parsing, and UI rendering intact.
- Leave commit list and commit patch endpoints unchanged, since they already represent commit history rather than worktree-to-worktree file review.

**Non-Goals:**

- Redesign the Diff UI or replace the existing diff renderer.
- Change commit-range semantics for `GET /api/issues/:number/commits` or `GET /api/issues/:number/commits/:hash/diff`.
- Introduce persistent snapshots, cached diffs, or new review storage.
- Fully solve the "branch is behind base" UX in this change; at most, the design should preserve room for a future warning.

## Decisions

### D1: Use two-argument `git diff <base> <head>` for issue-level file review

Issue-level file review will switch from three-dot syntax to two-argument diff syntax in both the API and CLI. This makes the review surface compare base HEAD vs issue HEAD directly, which matches the PR-style mental model expected by reviewers and cancels out changes already present on both sides after merge-forward workflows.

This is the smallest change that fixes the wrong file set without changing the response contract or the rest of the review stack.

**Alternatives considered:**

- Keep three-dot diff and try to post-filter merged base-branch files: rejected because it is harder to reason about, easier to get wrong, and duplicates git's own comparison semantics.
- Recompute a synthetic merge-base after each merge from base: rejected because it changes branch management assumptions and does not address the core requirement of reviewing current base-vs-head worktree differences.
- Use `git diff $(git merge-base base head) head` explicitly: rejected because it is equivalent to the current wrong behavior.

### D2: Change `--numstat` and full patch generation together from one shared diff range

The API currently derives summary counts from `git diff --numstat` and per-file patches from a second `git diff` call, both built from the same `diffArgs`. The implementation should keep that structure, but both calls must switch together to the new two-argument range so summary numbers and expanded file patches stay aligned.

This preserves the existing parsing code and minimizes implementation risk.

**Alternatives considered:**

- Change only the full diff call and leave `--numstat` unchanged: rejected because file counts and additions/deletions would disagree with the visible patch set.
- Replace the current two-call approach with a custom parser over one raw git command: rejected because it expands scope without solving a current correctness gap.

### D3: Do not change commit review endpoints in this fix

The issue-level Files changed view is the broken surface. Commit-level review already uses `git log base..head` and `git show`, which are intentionally commit-history oriented and do not suffer from the same merge-base contamination problem described in this issue.

Leaving commit review untouched keeps the fix narrowly scoped and avoids accidental regressions in the commit narrative feature.

**Alternatives considered:**

- Normalize all review-related git commands to the same syntax: rejected because commit history and worktree diff answer different questions and should not be forced into one model.

### D4: Preserve current unavailable-state handling and defer behind-base UX to a follow-up

The current API already distinguishes `not_started`, `worktree_removed`, `branch_missing`, and `git_error`. This change should not alter those states. If the issue branch is behind the base branch, two-argument diff may show base-only changes as deletions from the issue branch perspective; that behavior is a truthful consequence of the selected comparison model.

For this change, the design keeps the raw behavior and leaves any explicit "branch is behind base, merge first" warning as a future UI enhancement.

**Alternatives considered:**

- Add behind-base detection and a new response field now: rejected for this change because the issue can be fixed correctly without expanding the API contract.
- Suppress behind-base output heuristically: rejected because it would hide real divergence and create a less trustworthy review surface.

## Risks / Trade-offs

- [Two-argument diff can show base-only changes when the issue branch is behind base] -> Accept in this change and leave room for a future warning or merge-ahead affordance.
- [API and CLI could drift again if one command changes and the other does not] -> Update both call sites in the same change and verify them against the same issue branch scenario.
- [Existing tests may not cover merge-forward histories] -> Add or update focused tests around issue diff generation using a branch that merges the base branch before adding issue-only commits.

## Migration Plan

1. Update `packages/cli/src/api/issues.ts` to build diff arguments as `['diff', project.baseBranch, branchName]`.
2. Keep the existing `--numstat` and full patch parsing flow, but run both with the new diff arguments.
3. Update `packages/cli/src/cli/commands/issue.ts` so `mo issue diff <number>` runs `git diff <baseBranch> <branchName>`.
4. Add or adjust tests that exercise issue diff behavior after merging the base branch into the issue branch.
5. Verify manually against the known reproduction case that the issue diff shrinks from the polluted file set to the issue-owned file set.

Rollback is straightforward: restore the previous three-dot diff arguments in the two call sites if unexpected review regressions are found.

## Open Questions

- Should a future API revision expose an explicit `behindBase` flag so the Web UI can explain why a two-argument diff contains apparent deletions from base-only changes?
