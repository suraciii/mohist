## Why

Mohist currently conflates delivery completion with workspace cleanup: after a merge succeeds, the issue becomes Done but the local worktree may already be removed. This breaks the user's expectation that Done remains inspectable for audit, debugging, diff review, and traceability, while Archive is the explicit user action that hides the issue and cleans the retained local workspace.

## What Changes

- Stop automatically removing issue worktrees after merge queue success or manual merge API success; successful merge moves the issue to Done with `mergeState=merged` while preserving the worktree for inspection.
- Keep archive as the default cleanup boundary: archiving an issue hides it from default board/list views and removes retained worktree/checkpoint state while preserving history, comments, logs, and review artifacts.
- Treat archive as a one-way organization action for this change; do not provide unarchive, worktree restore, checkpoint restore, or archived issue re-execution flows.
- Make single-issue archive allow Done-but-not-merged issues with clear warning/error feedback, while batch archive skips Done issues that are not confirmed merged.
- Make batch archive available from the Done column whenever there are archivable Done issues, not only when archived issues already exist.
- Return and display batch archive results with archived count, skipped count, and skipped issue numbers so users understand unmerged Done issues require individual handling.
- Update Done issue UI copy so retained worktrees are presented as review/traceability context, not as a normal continued-development entry point.
- Normalize CLI archive warning output so warnings are not duplicated as `Warning: Warning:`.
- Clarify `--no-cleanup` semantics for single and batch archive paths so cleanup behavior is predictable.
- Add regression coverage for merge-retained worktrees, archive cleanup, archive filtering, false-Done archive warnings/skips, batch result reporting, Done-column archive affordance, and CLI warning formatting.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `worktree-manager` — Worktree cleanup semantics change from automatic cleanup on successful merge to retained Done worktrees that are cleaned by explicit archive cleanup.
- `http-api` — Archive, batch archive, manual merge, issue list, archived list, and worktree-related responses must expose and preserve the Done-vs-Archive boundary, false-Done warnings/skips, and cleanup outcomes.
- `cli-interface` — `mo issue archive`, `mo issue archive --all-completed`, and issue list/show output must reflect archived filtering, warning formatting, skipped false-Done issues, and cleanup option semantics.
- `web-ui` — Kanban, Done column actions, issue detail, worktree panel, archive action feedback, and Archived page must communicate retained Done worktrees, hide archived issues by default, show archive warnings/errors, and omit restore actions.
- `local-issue-store` — Archived issue filtering and archived history access remain part of the data contract, with default queries excluding archived issues and archived queries preserving historical issue data.

## Impact

- **Merge flow**: `packages/cli/src/git/merge-queue.ts` and the merge-success callbacks should no longer call `WorktreeManager.remove()` after a successful merge.
- **Manual merge API**: `packages/cli/src/api/issues.ts` `POST /api/issues/:number/merge` should preserve the worktree on success and return state consistent with Done inspection.
- **Archive API/service**: `packages/cli/src/services/issue-service.ts` and `packages/cli/src/api/issues.ts` archive paths must keep cleanup as the explicit archive responsibility, report warnings/errors, skip false-Done issues in batch archive, and return `archived`, `skipped`, and `skippedNumbers`.
- **Storage/querying**: `packages/cli/src/db/issue-repo.ts` default issue queries and archived-only queries are part of the visible behavior and need tests for default hiding plus archived history retrieval.
- **Worktree/checkpoint cleanup**: `packages/cli/src/git/worktree-manager.ts` and `PipelineCheckpointRepo` cleanup calls remain used by archive cleanup, not by Done transition cleanup.
- **CLI**: `packages/cli/src/cli/commands/issue.ts` must align `archive`, `--all-completed`, and `--no-cleanup` output with API results and avoid duplicate warning prefixes.
- **Web UI**: `packages/cli/web/src/components/StageColumn.tsx`, `IssueCard.tsx`, `IssueDetailPage.tsx`, `WorktreePanel.tsx`, `ArchivedPage.tsx`, and related hooks/API types must update archive affordances, retained-worktree copy, warning/error presentation, and remove unarchive/restore actions from this flow.
- **Tests**: Update `packages/cli/tests/issue-archive.test.ts`, merge/manual-merge tests, CLI archive formatting tests, and Web UI tests around Done archive controls, Archived page behavior, and retained worktree messaging.
