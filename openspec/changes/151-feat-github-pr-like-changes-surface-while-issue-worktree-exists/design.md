## Context

Issue Detail already has a Changes panel backed by `GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff`. The current route handlers return shallow success payloads such as `{ files: [] }` or `{ commits: [] }` when the issue worktree is missing, which makes a cleaned-up completed issue indistinguishable from a not-started issue. The commits route also parses `git log --stat` with an in-header `\x01` separator, so stat output can be split away from the commit header and later commits can be dropped.

The existing frontend has the right basic pieces: `IssueDetailPage` loads diff and commits, `ChangesPanel` renders Files and Commits tabs, and `DiffViewer` can render git patches with line numbers. The change should deepen the existing API/UI surface rather than introduce a new persistence model or external PR abstraction. Worktree existence remains the lifecycle boundary: while the worktree exists, review data is generated from git; after it is removed, diff and commit data are intentionally unavailable.

## Goals / Non-Goals

**Goals:**

- Provide one reliable review-data contract for issue diff, commit history, and commit patch availability.
- Return explicit unavailable reasons for removed worktrees, missing branches, not-started issues, and git failures.
- Compute complete base/head metadata and summary counts while the issue worktree exists.
- Fix commit parsing so the complete `base..head` range is returned with per-commit stats and touched files.
- Make the Issue Detail Changes surface files-first, with commit history as a companion view and expandable commit patches.
- Reuse the existing `DiffViewer` for all patch rendering.

**Non-Goals:**

- Persist full diff snapshots after worktree cleanup.
- Preserve review data for archived or cleaned issues.
- Add GitHub PR synchronization, remote PR creation, review comments, or conversation threads.
- Add a new external diff rendering dependency.
- Treat agent session or pipeline data as a substitute for git commit history.

## Decisions

### D1: Keep Review Data On The Existing Issue API Routes

The existing issue routes will keep serving review data, but their success payloads will become availability-aware. This keeps the frontend on the same query model while making the API deep enough to explain lifecycle states.

Use a shared response envelope inside each review payload:

```ts
type ChangesUnavailableReason = 'worktree_removed' | 'branch_missing' | 'not_started' | 'git_error'

type ChangesAvailability =
  | { available: true; reason: null }
  | { available: false; reason: ChangesUnavailableReason; message: string }

type ChangesSummary = {
  filesChanged: number
  commits: number
  additions: number
  deletions: number
}
```

`GET /api/issues/:number/diff` should return `available`, `reason`, `base`, `head`, `summary`, and `files` when available. `GET /api/issues/:number/commits` should return the same availability and branch metadata, a compatible summary, and `commits`. `GET /api/issues/:number/commits/:hash/diff` should return availability plus `hash` and `diff` when available.

**Alternatives considered:** Add a new `GET /api/issues/:number/review` endpoint that aggregates everything. This would reduce frontend request count, but it would duplicate existing routes and make commit patch expansion less lazy. Keeping the existing endpoints is smaller and preserves lazy commit diff loading.

### D2: Derive Availability From Issue Stage, Worktree Existence, And Branch Checks

The API should distinguish empty-but-valid from unavailable. For Backlog or otherwise never-started issues with no worktree, return `available: false`, `reason: 'not_started'`, and a message suitable for `No changes yet`. For Done or later lifecycle states with no worktree, return `available: false`, `reason: 'worktree_removed'`. When a worktree exists but `mo/issue-<number>` or the configured base branch is missing, return `branch_missing`. Unexpected git command failures should become `git_error` in the review payload rather than an ambiguous empty list.

Worktree checks should continue to use `WorktreeManager.exists(project.name, issue.number)`. Branch existence can be checked with git commands in `project.path` before running range operations, because the diff and log commands are comparing repository refs rather than reading only the worktree directory.

**Alternatives considered:** Treat every missing worktree as `worktree_removed`. That would satisfy cleaned Done issues but would mislabel Backlog issues as having lost review history. Treat missing worktrees as HTTP errors. That would force UI error handling for normal lifecycle states.

### D3: Use Git Plumbing That Preserves Commit Boundaries

The commits route should stop splitting combined `git log --stat` output on a marker placed before stat text. Use a format that emits an unambiguous commit-start record, then parse each commit including its following stat lines. A practical shape is:

```text
git log base..head --date=iso-strict --numstat --format=<record-separator>%H%x00%h%x00%s%x00%an%x00%aI
```

`--numstat` should be preferred over human `--stat` for additions/deletions and file names because it is simpler to parse and matches the diff endpoint's counting model. Each commit entry should include full hash internally if needed for validation, short hash for display, message, author, date, `filesChanged`, additions, deletions, and touched file paths. The response should include the complete range in git log order.

**Alternatives considered:** Keep `--stat` and change the delimiter. That can fix the immediate `\x01` bug but still leaves file/stat parsing dependent on localized or human-oriented summary lines. Use one git command per commit. That is simpler per entry but increases process overhead for multi-commit issues.

### D4: Compute Review Summary From The Diff Range, Not From UI Aggregation

The authoritative files/additions/deletions summary should come from the diff API's `git diff base...head --numstat` parse. The commit count should come from `git rev-list --count base..head` or the parsed commit list. Frontend components should display server-provided summary values and only use local reduction as a fallback during incremental migration.

This keeps the base/head summary consistent between the top Issue Detail summary and the Changes panel header.

**Alternatives considered:** Continue computing totals in `ChangesPanel` from the loaded `files` array. That works for additions/deletions but does not cover unavailable states, branch metadata, or commit count without coordinating multiple query responses in the component.

### D5: Make Files The Default Review Tab In IssueDetailPage

`IssueDetailPage` should initialize `diffTab` to `'files'`. `ChangesPanel` should receive availability-aware diff and commit query data, render the server-provided base/head and summary header, and then show Files changed before Commits.

The panel should use specific state rendering:

- `not_started` with no worktree: show `No changes yet`.
- available diff with no files: show `No file changes yet`.
- available commits with no commits: show `No commits yet`.
- `worktree_removed`: show `Changes unavailable` with workspace-removed copy.
- `branch_missing`: show branch-missing copy.
- `git_error` or query error: show failed-to-load copy.

**Alternatives considered:** Keep Commits as the default because commits tell the agent work narrative. The proposal and acceptance criteria require GitHub PR-like review evidence, where files changed are the review subject and commits are supporting narrative.

### D6: Reuse DiffViewer And Keep Commit Patch Loading Lazy

File diffs should continue using the diff text already included per `DiffFile`. Commit diffs should continue loading only when a commit is expanded through `useCommitDiff(issueNumber, hash, expanded)`. `DiffViewer` remains the only patch renderer.

If `GET /commits/:hash/diff` returns unavailable, `CommitRow` should render the payload message rather than only relying on React Query's generic error state. If a commit diff is available but empty, show a small empty patch message instead of hiding the expanded area.

**Alternatives considered:** Include every commit patch in the commits response. This would make expansion instant but would produce large responses and duplicate patch data for users who only need the commit list.

### D7: Keep Types Explicit In `lib/types.ts`

Frontend types should model availability as discriminated unions rather than optional fields sprinkled across existing interfaces. Add response types such as `IssueDiffResponse`, `IssueCommitsResponse`, and `CommitDiffResponse`, while keeping `DiffFile`, `CommitEntry`, and `CommitDiff` as the data records.

The API client in `lib/api.ts` and hooks in `useQueries.ts` should return those response types directly so components do not infer availability from array length.

**Alternatives considered:** Add optional `available`, `reason`, and `message` to the existing ad hoc `{ files }` and `{ commits }` object types inline in `api.ts`. That is less work initially but repeats the same state model across hooks and components.

## Risks / Trade-offs

- [Risk] The diff and commits endpoints may briefly disagree if the branch changes between separate requests. → Mitigation: compute each response from the same base/head refs and keep UI tolerant by preferring diff summary for file stats and commit response for commit count.
- [Risk] Large diffs can make `GET /diff` heavy because it includes per-file patch text. → Mitigation: preserve current behavior for file diffs, keep commit patches lazy, and avoid adding all commit patch data to the commits list.
- [Risk] Branch-missing detection can misclassify transient git states during rebase or cleanup. → Mitigation: check worktree existence first, then refs, and return explicit messages that identify which ref is missing.
- [Risk] Availability-aware payloads change frontend response shapes. → Mitigation: update `api.ts`, `useQueries.ts`, and `types.ts` together, and keep backend responses under `success: true` for expected lifecycle states.
- [Risk] Git command failures could expose low-level messages in the UI. → Mitigation: log raw errors server-side and return concise user-facing `message` values in `git_error` payloads.

## Migration Plan

1. Add shared review response types in `packages/cli/web/src/lib/types.ts` and update the API client return types.
2. Update issue API route handlers to return availability-aware payloads for diff, commits, and commit diff.
3. Replace the commits parser with a `--numstat` parser that preserves commit boundaries and returns the full range.
4. Update `useQueries.ts` consumers and `IssueDetailPage` to pass full response objects into `ChangesPanel` and default the tab to Files.
5. Update `ChangesPanel` and `CommitRow` to render summary metadata, specific empty/unavailable states, files-first content, and lazy commit patches through `DiffViewer`.
6. Verify with a multi-commit retained-worktree issue and a cleaned Done issue; add or update tests around commit parsing and unavailable responses where route tests already exist.

Rollback is straightforward because no data migration is involved: revert the API response shape and frontend consumers together. No stored issue or project data is changed.

## Open Questions

- Should `GET /api/issues/:number/commits/:hash/diff` use the short hash supplied by the UI or normalize to the full hash returned by the commits endpoint before validation? Either works with git, but full hashes reduce ambiguity.
- Should archived issues hide the Changes panel entirely at the page level, or should the panel render the same `worktree_removed` unavailable state when directly accessed? The product text prefers no PR-like review surface for archived issues, but direct-route behavior should be made consistent in the UI spec.
