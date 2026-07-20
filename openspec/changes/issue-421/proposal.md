## Why

After work is split into sub-issues, the shared requirement background, overall goal, and acceptance criteria remain on the parent issue, while each child describes only its own delivery scope. Plan agents currently see only the child issue, forcing users to duplicate parent material into every child or accept plans made without the requirement's full context.

## What Changes

- When a sub-issue enters the Plan stage, its Inline Agent receives the parent issue's title and body as clearly identified background context alongside the child issue's own body and comments.
- The parent context is read-only reference material; the child issue's body remains authoritative for that child's delivery scope.
- Parent comments and artifacts are not included, and no context is shared from sibling sub-issues.
- Plan input for issues without a parent remains unchanged.
- Workflow lifecycle, stage progression, approvals, and parent-child status rules remain unchanged.

## Capabilities

- `sub-issue-plan-context`: Plan-stage Inline Agent context for sub-issues, including the parent title and body as background, preserving the child body as scope authority, excluding parent comments/artifacts and sibling issues, and preserving existing Plan behavior for ordinary issues.

## Impact

- **Server**: Issue read-side and Plan dispatch/prompt assembly must resolve the current child's parent and provide only the parent's title and body to Plan-stage Inline Agent work.
- **Runner contract**: the server-to-runner work input gains the optional related-issue background needed to present parent context to the Inline Agent; non-Plan and non-child dispatches do not gain effective context.
- **Runner**: `mohist/opencode` Plan turns consume the optional background without changing the child issue prompt or task completion contract.
- **Tests**: server and runner coverage must distinguish child Plan dispatch, ordinary Plan dispatch, non-Plan dispatch, and exclusion of parent comments and artifacts.
- **Public surfaces and dependencies**: no CLI, Web, public Issue API, persistence schema, or external dependency changes.
