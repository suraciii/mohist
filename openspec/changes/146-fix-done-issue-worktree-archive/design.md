## Context

The current implementation has the right archive primitives but the cleanup boundary is split across multiple flows. `MergeQueue` removes the issue worktree immediately after successful merge, and the manual merge API also removes the worktree after `mergeBack()`. `IssueService.archive()` separately marks `archivedAt` and calls `performCleanup()` to remove worktrees and checkpoints, while default issue queries already hide archived issues unless `archived=true` or `all=true` is requested.

This change makes `Done` and `Archive` separate lifecycle facts. `Done` means delivery is complete and still inspectable. `Archive` means the user has intentionally removed the issue from the default working set and cleaned retained transient local state. The implementation should keep this boundary in one place: merge paths update delivery state only; archive paths perform cleanup.

## Goals / Non-Goals

**Goals:**

- Preserve issue worktrees after merge queue success and manual merge API success.
- Keep archive cleanup as the explicit cleanup boundary for retained worktrees and checkpoints.
- Hide archived issues from default list/kanban queries while preserving archived history access.
- Make single archive warnings and batch archive skipped results visible in Web and CLI.
- Ensure batch archive only archives Done issues that are confirmed merged.
- Remove restore/unarchive affordances from the Web archive experience and avoid extending restore behavior.
- Keep the implementation small by reusing `classifyMergeDelivery()`, `IssueService.archive()`, `archiveAllCompleted()`, and existing archived query flags.

**Non-Goals:**

- Do not implement unarchive, worktree restore, checkpoint restore, or archived issue re-execution as user-facing flows.
- Do not add a new database stage or schema migration.
- Do not delete historical comments, logs, sessions, review output, or archived issue rows during archive.
- Do not change merge queue conflict/rebase algorithms beyond the cleanup timing.
- Do not automatically repair historical false-Done issues.

## Decisions

### D1: Merge success never calls worktree cleanup

Remove `WorktreeManager.remove()` calls from successful merge paths. In `packages/cli/src/git/merge-queue.ts`, after `mergeBack()` succeeds, set `MergeState.Merged`, emit `merge_completed`, call `onMergeSuccess`, and leave the worktree intact. In `packages/cli/src/api/issues.ts` `POST /api/issues/:number/merge`, remove the post-merge cleanup block and return success with the worktree still present.

This keeps merge code focused on delivery state and avoids a hidden side effect that surprises users at the exact moment they want to inspect the result. Logs should also be renamed from cleanup wording to retention wording, for example `Fast-forward merge succeeded; retaining worktree until archive`.

**Alternatives considered:** Keep cleanup after merge and add a separate archived copy of diffs/logs. That preserves disk usage but does not preserve the actual review/debug workspace and adds a second audit surface. Retaining the worktree until archive is simpler and matches user intent.

### D2: Archive cleanup remains owned by `IssueService.performCleanup()`

Keep cleanup centralized in `IssueService.archive()` through `performCleanup()`. The service already has access to `ProjectRepo`, `WorktreeManager`, and `PipelineCheckpointRepo`, and it already treats cleanup failures as warnings in logs rather than failing archive state. Preserve that shape: archive first marks the issue archived, then best-effort removes worktree and checkpoints when cleanup is enabled.

The archive API should return enough information for clients to explain what happened: `issue`, `message`, optional `warning`, and a cleanup-related warning or error only if cleanup failures become part of the public contract. The current minimum is to surface the existing false-Done/non-Done warning reliably.

**Alternatives considered:** Move cleanup from `IssueService` into route handlers or UI-specific endpoints. That would spread cleanup knowledge across API, CLI, and Web paths and make `--no-cleanup` behavior harder to keep consistent. Keeping service ownership makes archive a deep operation with a simple interface.

### D3: Use delivery classification for archive safety

Use `classifyMergeDelivery(issue)` as the archive trust gate. Single archive continues to allow a false-Done issue but returns a prominent warning. Batch archive only archives issues whose delivery status is `merged`; it skips `done-not-merged` and reports `skipped` plus `skippedNumbers`.

`archiveAllCompleted()` should filter candidates to Done, non-archived issues, then apply the classifier before calling `archive()`. This avoids hiding anomalous Done issues through a bulk action while still letting a user intentionally archive a specific issue after seeing a warning.

**Alternatives considered:** Reject single false-Done archive. This is safer but too strict for a user who is deliberately organizing one issue and can inspect the warning. The chosen split follows the risk profile: single action is intentional and contextual; bulk action should be conservative.

### D4: `--no-cleanup` means mark archived but retain local transient state

Define `cleanup` consistently as `cleanup !== false`: when cleanup is enabled, archive removes worktree and checkpoints; when disabled, archive only sets `archivedAt` and leaves local state intact. CLI `--no-cleanup` should send `{ cleanup: false }` for single archive.

For batch archive, either support `--no-cleanup` by passing `{ cleanup: false }` through the archive-completed API to each archived issue, or explicitly reject/ignore it with a clear CLI/API message. The preferred implementation is to extend `archiveAllCompleted(projectId, { cleanup })` and `POST /issues/archive-completed` request body with the same `cleanup` boolean so one flag has one meaning everywhere.

**Alternatives considered:** Keep `--no-cleanup` only for single archive. That avoids an API signature change but leaves the same flag with different behavior depending on mode, which is a small but persistent source of user confusion.

### D5: Web archive mutations display backend feedback

Update the Web API types so `archiveIssue()` returns `{ issue, message, warning? }` and `archiveAllCompleted()` returns `{ archived, skipped, skippedNumbers, message }`. Mutation success handlers should show success messages and warning/skipped details via existing toast or inline state. Mutation error handlers should display `ApiError.message` instead of silently relying on query invalidation.

For single issue archive from an issue card, the minimal path is to show the backend warning after successful archive. If a stronger false-Done confirmation is desired, the UI can do a preflight from issue fields and show `window.confirm()` or a small dialog before calling archive, but the backend warning remains authoritative.

**Alternatives considered:** Let API warnings appear only in logs or CLI. That keeps the UI small but fails the core acceptance criterion that Web archive no longer silently swallows risk information.

### D6: Done column archive controls depend on Done issues, not archived count

Render the Done-column footer whenever `isDone` and either active Done issues exist or archived issues exist. The batch archive button should be visible when `totalCount > 0`, regardless of `archivedCount`. The archived link/count can still be conditional on `archivedCount > 0`.

This is a UI-only correction: the API already filters archived issues out of default queries. The Done column should not require a previous archive before offering the first archive action.

**Alternatives considered:** Put batch archive only in a global toolbar. That avoids the footer condition bug but separates the action from the Done column where users naturally expect cleanup for completed work.

### D7: Archived page is history-only for this change

Remove `useUnarchiveIssue()` from `ArchivedPage` and remove the restore button. Keep search, archived count, and links to issue detail. The backend unarchive endpoint and service method may remain as dead/legacy code unless a spec explicitly removes the API; this change only commits not to expose or expand restore behavior in the current archive UX.

**Alternatives considered:** Delete backend unarchive immediately. That is cleaner semantically but broader and riskier because CLI and tests currently reference unarchive. Hiding Web restore satisfies the scope while avoiding unrelated API breakage.

### D8: Retained worktree UI copy is inspect-only on Done

When `WorktreePanel` or issue detail shows a worktree for a Done issue, add copy that explains the worktree is retained for review, traceability, diff inspection, and debugging, and that archiving will remove it. Avoid showing Done worktree controls as a normal continued-development path. Rebase/rerun controls should remain governed by existing stage/status rules and should not be introduced for archived issues.

**Alternatives considered:** Hide the worktree panel on Done to avoid implying continued development. That also hides the proof users want after delivery. Inspect-only copy keeps the useful context while setting expectations.

## Risks / Trade-offs

- [Risk] Retaining merged worktrees increases disk usage until users archive. → Make archive affordances visible from Done and clearly state that archive removes retained worktrees.
- [Risk] Cleanup failures after marking archived can leave hidden issues with retained worktrees. → Keep cleanup warnings logged and, if practical, return cleanup warnings in archive responses; users can still use explicit cleanup endpoints or manual cleanup.
- [Risk] Web and CLI may interpret batch archive results differently. → Return a single response shape with `archived`, `skipped`, `skippedNumbers`, and `message`, and make both clients print/display the server message.
- [Risk] Existing tests may assert worktree removal after merge. → Update tests to assert retention after merge and cleanup after archive instead.
- [Risk] Keeping backend unarchive while hiding Web restore leaves a latent API path. → Treat it as legacy behavior outside this change; do not add UI affordances or new restore tests unless a future change redefines archive reversal.
- [Risk] `archiveAllCompleted` may archive Done issues with `status` other than completed if it only filters by stage. → Require `classifyMergeDelivery(issue) === 'merged'` for batch archive, which covers `stage/status/mergeState` trust rather than stage alone.

## Migration Plan

1. Remove post-success worktree removal from `MergeQueue` and manual merge API; update merge logs to mention retention until archive.
2. Keep `IssueService.performCleanup()` as the archive cleanup implementation and ensure it removes worktrees and checkpoints only from archive paths.
3. Extend archive result types and API responses for warnings and batch `skippedNumbers`; add optional cleanup body support to `archive-completed` if batch `--no-cleanup` is supported.
4. Update CLI archive command to pass cleanup consistently, print server batch messages, and avoid adding `Warning:` when the server warning already includes it.
5. Update Web API client/hooks/types for archive response shapes, success/warning/error toasts, Done-column batch archive visibility, and Archived page history-only behavior.
6. Update Done issue detail/worktree copy to say retained worktrees are for review/traceability and archive removes them.
7. Add or adjust tests for merge retention, manual merge retention, archive cleanup, default archived filtering, archived list access, single false-Done warning, batch false-Done skips, batch archive response shape, Done-column archive visibility, Archived page no-restore UI, and CLI warning de-duplication.

Rollback is code-only because no schema changes are required. If needed, restore merge-time cleanup calls and previous UI/CLI behavior together; archived issue rows and retained worktrees created during the new behavior can be manually archived or cleaned with existing cleanup paths.

## Open Questions

- Should archive cleanup failures be exposed as public API warnings in addition to logs, or is best-effort cleanup with server logs sufficient for this change?
