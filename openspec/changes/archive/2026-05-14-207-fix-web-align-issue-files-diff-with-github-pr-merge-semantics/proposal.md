## Why

Mohist's issue changed-files view currently uses a base-vs-head two-dot diff, so when an issue branch is behind base it mixes base-only changes into the file list and misstates what the issue will actually merge. Users also cannot see the issue's commit history directly on Issue Detail, which makes it harder to understand what work this issue consists of and whether the current branch state is converging.

## What Changes

- Change the default issue diff semantic from current base-vs-head comparison to merge-base-to-issue-head comparison so `Files changed` matches GitHub PR merge semantics.
- Expose diff semantic metadata in issue diff responses, including the merge base and ahead/behind relationship needed to explain what is being shown.
- Make Issue Detail describe the merge relationship explicitly and add a first-class commits section showing the commits that would merge from the issue branch.
- Reshape the changed-files page header and reading controls around a PR-like `wants to merge into` framing, with a non-blocking notice when the issue branch is behind base.
- Keep commit-specific diff inspection as a secondary reading mode without changing the default `Files changed` meaning away from the merge-base diff.

## Capabilities

### New Capabilities

<!-- Leave empty if none. -->

### Modified Capabilities

- `issue-changed-files-reader` - change the page's default contract from final base-vs-head differences to the merge-base-to-head change set the issue would contribute, and align the page framing with PR-style merge semantics.
- `issue-review-surface` - make commit history a first-class part of understanding an issue's pending merge content rather than an optional deep mode inside file reading.
- `http-api` - change issue diff and commit payload requirements to return merge-base comparison data, semantic metadata, and consistent summary counts across detail and files surfaces.
- `web-ui` - change Issue Detail and changed-files summary behavior to describe merge intent, surface issue commits, and explain behind-base branches without implying base-only changes belong to the issue.

## Impact

- `packages/cli/src/api/issues.ts` - `GET /api/issues/:number/diff` currently uses `git diff <base> <head>` while `GET /api/issues/:number/commits` already mixes `base..head` and `base...head`; both endpoints will need a shared merge-base semantic and response metadata.
- `packages/cli/web/src/components/IssueDetailPage.tsx` - the current summary row shows base/head counts but does not present merge framing or a commit list section.
- `packages/cli/web/src/components/IssueChangedFilesPage.tsx` - the current page header and toolbar still frame the reader as generic base-to-head diff browsing and surface commit mode as a peer control.
- `packages/cli/web/src/lib/types.ts`, `packages/cli/web/src/lib/api.ts`, and `packages/cli/web/src/hooks/useQueries.ts` - response types and client queries will need to carry diff semantic metadata consistently.
- Existing OpenSpec capabilities in `openspec/specs/issue-changed-files-reader/spec.md`, `openspec/specs/issue-review-surface/spec.md`, `openspec/specs/http-api/spec.md`, and `openspec/specs/web-ui/spec.md` all describe behaviors that this change updates.
