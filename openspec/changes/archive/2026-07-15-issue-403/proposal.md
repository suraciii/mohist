## Why

The Files/Diff evidence page (`/issues/<number>/files`) is a dead end when evidence cannot be loaded. Today it has two distinct failure paths, and neither is recoverable: a query/transport error renders a bare `ErrorState` banner that strips issue context and offers only a navigation link, while a server-reported unavailability (e.g. runner disconnected, workspace removed) renders an orange banner with no retry and no next step. Reloading the page can strand the user in the same broken state. Evidence surfaces must preserve issue context and give the user a path forward — retry, return to the issue, or open the related session — instead of presenting a raw failure with no way out.

## What Changes

- Replace the dead-end error states on the Files/Diff page with a single **recoverable error surface** that keeps the issue context visible (issue number, title, health badge) alongside the failure explanation.
- Explain load failures in **product language** ("The file changes for this issue could not be loaded. The runner may be disconnected.") rather than surfacing raw error identifiers or HTTP status as the user's primary guidance.
- Add a **retry** action to the error surface that re-fetches the failed evidence sources (following the per-source retry precedent already established on the Activity page).
- Keep and make prominent the **return-to-issue** navigation from the error surface.
- Add **related-session awareness** to the error surface: when the issue has a known workflow-run session, offer a link to open it. This is net-new for the page — it currently has no session knowledge at all.
- The recoverable surface applies to both failure paths — query/transport errors (HTTP failures, network) and server-reported unavailability (`runner_unavailable`, `workspace_removed`, `branch_missing`, `git_error`, `not_started`) — so the user is never stranded regardless of why the evidence is unreachable.

## Capabilities

- `changed-files-recovery`: The recoverable failure state for the Files/Diff evidence page — when evidence cannot be loaded (connection disconnected, runner unavailable, query failure, or any server-reported unavailability reason), the page preserves issue context, explains the failure in product language, and offers retry, return-to-issue, and related-session-link actions. Covers both the transport-error path and the server-reported-unavailability path.

## Impact

- **Web (`packages/web`)**:
  - `pages/issue-changed-files/ui/IssueChangedFilesPage.tsx` — the top-level state machine (`ErrorState`, `InvalidIssueState`, and the `getDiffAvailability` unavailable branch) is the primary change site; the new recoverable surface replaces the bare error banners and adds retry + session-link actions.
  - `pages/issue-changed-files/ui/IssueChangedFilesPage.fixture.tsx` — fixture harness already simulates every failure mode (`blockIssue`/`blockDiff`/`blockCommits` flags + availability reasons); will be extended for retry and session-link scenarios.
  - `pages/issue-changed-files/ui/IssueChangedFilesPage.recovery.test.tsx` — existing recovery tests document current dead-end behavior; will be updated to assert the new recoverable surface (retry, context preservation, session link).
  - `widgets/issue-changed-files/ui/FullFilePane.tsx` — has its own fetch error with no retry; in scope if the full-file fetch failure is part of the recoverable surface.
  - `entities/issue/api/queries.ts` — `useIssue`/`useIssueDiff`/`useIssueCommits`/`useCommitDiff` refetch functions provide the retry mechanism; `useWorkflowRunSessions` (already used by `IssueDetailPage`) provides session resolution.
  - `pages/issue-detail/ui/sections/IssueDiffFilesSection.tsx` / `IssueCommitsSection.tsx` — inline sections that silently return `null` when unavailable; may surface a lightweight recovery hint, though the primary fix targets the full page.
- **Server / runner / CLI**: no changes — the unavailability reasons and REST endpoints already exist; this is a web-only recovery-UX change.
- **Dependencies**: no new external dependencies; reuses TanStack Query refetch and existing session-resolution hooks.
- **Tests**: spec tests for the recoverable error surface (both failure paths), retry re-fetches evidence, issue context preserved, return-to-issue navigation, and related-session link when a session exists (and its absence when none exists).
