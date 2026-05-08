## Context

Issue comments already exist in the local issue store and are returned with issue detail responses. The current path is append-only: `IssueService.createComment()` and `getCommentsByIssue()` wrap `CommentRepo`, `POST /api/issues/:number/comments` exposes creation, `mo issue comment <number> <text>` calls that API, and Web issue detail renders comments with an add form.

The persistence layer already has `CommentRepo.findById(id)` and `CommentRepo.delete(id)`, so this change should not introduce schema changes. The missing design work is to expose deletion through the same service/API/CLI/Web layers while centralizing the issue/comment ownership check so no client can delete a comment from another issue by id.

## Goals / Non-Goals

**Goals:**

- Allow users to delete a single comment from a specific issue through CLI and Web.
- Keep the existing comment-add command compatible.
- Make comment ids visible in `mo issue show` so CLI users can target the correct comment.
- Ensure deletion validates current project context, issue existence, comment existence, and comment ownership.
- Return clear, consistent errors for missing issues and comments.

**Non-Goals:**

- Do not add issue deletion, close/archive/delete semantic changes, or bulk comment deletion.
- Do not add comment editing, soft deletion, restore, audit history, or a permission model.
- Do not change the comments database schema.
- Do not require real-time SSE propagation for comment deletion in this change.

## Decisions

### D1: Delete Comments Through `IssueService`

Add a service method such as `deleteComment(issueId: string, commentId: string): boolean` that looks up the comment, verifies `comment.issueId === issueId`, and only then calls `CommentRepo.delete(commentId)`. The API route will still resolve the project and issue by number before calling the service, matching the existing create-comment flow.

This keeps repository methods small and persistence-focused while putting ownership rules in the service layer where issue-level business rules already live. It also prevents CLI and Web handlers from each reimplementing the same comment ownership check.

**Alternatives considered:** Put `deleteByIssueAndId(issueId, commentId)` in `CommentRepo`; rejected because the repo would then encode a business-level not-found distinction that the service/API need to report. Let each API/client validate ownership directly; rejected because it duplicates a security-sensitive rule.

### D2: Expose a RESTful Delete Endpoint Under the Issue

Add `DELETE /api/issues/:number/comments/:commentId`. The handler will follow the current issue route pattern: resolve current project id, parse issue number, return 400 if there is no active project, return 404 if the issue is not found, call `issueService.deleteComment(issue.id, commentId)`, and return 404 if the service reports no deletion.

Successful deletion should return a compact response such as `{ message: "Deleted comment <id> from issue #<number>" }`. Treating "comment missing" and "comment belongs to another issue" as the same 404 avoids leaking cross-issue comment ids and keeps the API contract simple.

**Alternatives considered:** Use `DELETE /api/comments/:commentId`; rejected because it lacks the issue number context needed for safe ownership validation and user-facing error messages. Return 204 with no body; rejected because CLI and Web already use JSON `ApiResponse` wrappers and the CLI needs a success message.

### D3: Add a Dedicated CLI Delete Command Without Reworking Comment Add

Keep `mo issue comment <number> <text>` as the compatible append-comment command. Add a separate command, preferably `mo issue delete-comment <number> <comment-id>`, that calls the new DELETE endpoint and prints `Deleted comment <id> from issue #<number>` on success.

Using a top-level `delete-comment` subcommand avoids ambiguity with the existing positional `comment <number> <text>` command, where `delete` could otherwise be parsed as an issue number or command argument depending on commander matching behavior.

**Alternatives considered:** Add `mo issue comment delete <number> <comment-id>`; rejected for this change because it risks command parsing ambiguity with the existing `comment <number> <text>` signature unless the command group is refactored. Replace the existing add command with a nested command group; rejected because it is larger and creates unnecessary compatibility risk.

### D4: Display Short Comment Ids in CLI Output

Update `mo issue show <number>` to include an id next to each comment timestamp, using a short id derived from the stored UUID, such as the first 8 characters. The delete command should accept the id string the API can resolve; if short ids are accepted, service/API lookup must resolve them unambiguously within the issue.

The lowest-risk MVP is to display both a short label and the full id or to display the full id in a copyable form. If short-id deletion is implemented, ambiguity must produce a clear error instead of deleting an arbitrary match.

**Alternatives considered:** Display only timestamps; rejected because timestamps do not provide a reliable delete target. Display only full UUIDs; acceptable but less readable. Add sequential comment numbers; rejected because comment numbers are not persisted and can shift after deletions unless additional rules are added.

### D5: Web Uses Explicit Confirm Then Query Invalidation

Add `api.deleteComment(issueNumber, commentId)` and a mutation in `IssueDetailPage`. Each rendered comment gets a small Delete action. Clicking Delete asks for confirmation, disables the active delete action while the request is pending, and invalidates `['issues', issueNumber]` on success so the comment list refreshes from the server.

This matches the existing Web data flow for comment creation and avoids maintaining a second local copy of the comment list. Local removal can be used as an optimization later, but query invalidation is simpler and less error-prone for the initial feature.

**Alternatives considered:** Optimistically remove the comment before the server responds; rejected because failed deletes would need rollback state and the current comments UI does not require that complexity. Add a custom confirmation modal; optional, but a native confirmation is sufficient unless the existing UI already provides a reusable confirm dialog pattern nearby.

## Risks / Trade-offs

- [Short id collision] → Prefer full UUID deletion for MVP or resolve short ids only within the issue and reject ambiguous matches.
- [Accidental issue deletion confusion] → Use command naming, descriptions, confirmation text, and success messages that always say "comment" and include the issue number.
- [Cross-issue deletion by guessed id] → Perform ownership validation in `IssueService` before deleting and return 404 for comments outside the issue.
- [Stale Web UI after deletion] → Invalidate the issue detail query after successful deletion rather than relying on local state alone.
- [No real-time update across clients] → Accept manual/query refresh for this change; SSE deletion events can be a later enhancement if comment collaboration becomes important.

## Migration Plan

1. Add `IssueService.deleteComment(issueId, commentId)` using existing `CommentRepo.findById()` and `CommentRepo.delete()`.
2. Add `DELETE /api/issues/:number/comments/:commentId` using the same current-project and issue lookup pattern as existing issue comment routes.
3. Add CLI comment id display to `mo issue show <number>`.
4. Add `mo issue delete-comment <number> <comment-id>` and wire it to the DELETE endpoint.
5. Add Web API client support and the Issue detail delete action with confirmation, pending state, error handling, and issue query invalidation.
6. Add or update tests for service ownership validation, API 404 behavior, CLI output/command behavior, and Web mutation behavior where existing test infrastructure supports it.

Rollback is straightforward because there is no schema migration: remove the new API route and client commands/UI. Comments created before or after the change remain stored in the same table.
