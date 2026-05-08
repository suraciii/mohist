## Why

Issue comments are currently append-only, so mistyped, duplicated, outdated, or misleading comments remain permanently visible in issue detail views and `mo issue show`. Users need a safe way to maintain the quality of an issue's discussion history without confusing comment cleanup with deleting, closing, or archiving the issue itself.

## What Changes

- Add the ability to delete a specific comment from a specific issue through the backend service and REST API.
- Validate that the current project exists, the issue exists, the comment exists, and the comment belongs to that issue before deleting.
- Return clear not-found errors when the issue is missing or the comment does not exist under the requested issue.
- Add a CLI command for deleting an issue comment while keeping the existing comment-add command compatible.
- Update `mo issue show <number>` to display each comment's id or short id so users can identify the comment to delete.
- Add a lightweight delete action to each Web issue-detail comment, with confirmation, pending state, error display, and refreshed comment display after success.
- Keep comment deletion scoped to comments only; deleting a comment SHALL NOT delete the issue or comments on other issues.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- local-issue-store
- cli-interface
- http-api
- web-ui

## Impact

- `packages/cli/src/db/comment-repo.ts`: reuse existing comment lookup and delete operations as the persistence primitive for deleting comments.
- `packages/cli/src/services/issue-service.ts`: expose comment deletion behavior with issue/comment ownership validation.
- `packages/cli/src/api/issues.ts`: add `DELETE /api/issues/:number/comments/:commentId` and consistent 404 responses for missing issues or comments outside the issue.
- `packages/cli/src/cli/commands/issue.ts`: add the delete-comment CLI surface and include comment ids in `mo issue show` output.
- `packages/cli/web/src/lib/api.ts` and related hooks/components: add the frontend API call and issue-detail comment delete UI.
- `packages/cli/web/src/components/IssueDetailPage.tsx`: display per-comment delete controls, confirmation, pending/error states, and refresh or remove comments after deletion.
- Tests should cover service/API ownership validation, CLI output/command behavior, and Web delete success/failure states where existing test coverage supports it.
- No database schema changes or new runtime dependencies are expected.
