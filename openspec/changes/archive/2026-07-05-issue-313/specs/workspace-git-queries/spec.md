### Requirement: Workspace query resolution requires explicit head and base refs

The runner SHALL resolve a workspace query into a `{ workDir, baseBranch, head }` triple only when the query supplies a non-empty `workspacePath`, a non-empty `baseBranch`, and a non-empty `branch`. The resolver MUST NOT synthesize a head ref from `issueNumber` (the legacy `mo/issue-{N}` worktree branch is no longer materialized and MUST NOT be used as a fallback). When any of `workspacePath`, `baseBranch`, or `branch` is absent, the resolver SHALL return `null` so the server-side review APIs surface a `branch_missing` / unresolvable condition rather than querying a phantom ref.

#### Scenario: Explicit workspace, branch, and baseBranch resolve

- **WHEN** `resolveWorkspaceQuery` is called with `{ workspacePath, branch: "mohist/run-wr-25", baseBranch: "master" }`
- **THEN** it returns `{ workDir: workspacePath, baseBranch: "master", head: "mohist/run-wr-25" }`

#### Scenario: Missing baseBranch is rejected

- **WHEN** `resolveWorkspaceQuery` is called with `{ workspacePath, branch }` but no `baseBranch`
- **THEN** it returns `null` (it does not guess `main`/`master`)

#### Scenario: Missing branch is rejected with no mo/issue fallback

- **WHEN** `resolveWorkspaceQuery` is called with `{ issueNumber: 25, workspacePath, baseBranch: "master" }` but no `branch`
- **THEN** it returns `null` (it does not fall back to `mo/issue-25`)

#### Scenario: Missing or null query is rejected

- **WHEN** `resolveWorkspaceQuery` is called with `null`, `undefined`, or an empty object
- **THEN** it returns `null`

### Requirement: Git worktree presence is probed before any git query

Every git-backed handler SHALL probe that the resolved `workDir` is a real git worktree before issuing further git commands: the path MUST exist on disk AND `git rev-parse --is-inside-work-tree` MUST exit `0` with stdout trimmed to `true`. When the probe fails the handler SHALL return its not-found sentinel (`null` for `GetDiff` / `GetCommits` / `GetCommitDiff`, `{ exists: false }` for `GetWorkspaceStatus`, `{ base: null, head: null }` for `GetFileContent`).

#### Scenario: Non-existent workDir short-circuits to the not-found sentinel

- **WHEN** a git handler is invoked for a `workDir` that does not exist on disk
- **THEN** no `rev-parse --is-inside-work-tree` command is required to succeed and the handler returns its not-found sentinel without running diff/log/merge-base queries

#### Scenario: Path that exists but is not a worktree is treated as missing

- **WHEN** `workDir` exists on disk but `git rev-parse --is-inside-work-tree` does not return exit `0` with stdout `true`
- **THEN** the handler returns its not-found sentinel

### Requirement: Unresolvable workspace yields the not-found sentinel

When `resolveWorkspaceQuery` returns `null`, every git handler SHALL short-circuit to its not-found sentinel WITHOUT probing the filesystem or invoking git. `GetDiff` / `GetCommits` / `GetCommitDiff` MUST return `null`; `GetWorkspaceStatus` MUST return `{ exists: false }`; `GetFileContent` MUST return `{ base: null, head: null }`.

#### Scenario: GetDiff on an unresolvable workspace returns null

- **WHEN** `GetDiff` receives a query that `resolveWorkspaceQuery` rejects
- **THEN** the handler returns `null` and performs no git invocation

#### Scenario: GetWorkspaceStatus on an unresolvable workspace returns exists:false

- **WHEN** `GetWorkspaceStatus` receives a query that `resolveWorkspaceQuery` rejects
- **THEN** the handler returns `{ exists: false }`

#### Scenario: GetFileContent on an unresolvable workspace returns null base and head

- **WHEN** `GetFileContent` receives a query that `resolveWorkspaceQuery` rejects
- **THEN** the handler returns `{ base: null, head: null }`

### Requirement: GetDiff returns the per-file diff with binary markers and totals

`GetDiff` SHALL verify the head ref exists (`git rev-parse --verify refs/heads/<head>` exit `0`); a non-zero exit MUST return `null`. It SHALL then issue, in parallel, numstat, full diff, merge-base, ahead/behind count, and commit log against the range `<baseBranch>...<head>` and return `{ base, head, mergeBase, ahead, behind, commitCount, totalAdditions, totalDeletions, files }`. `mergeBase` SHALL fall back to `baseBranch` when `git merge-base` exits non-zero. `commitCount` SHALL fall back to `0` when the log exits non-zero. `totalAdditions` / `totalDeletions` SHALL be the sum across files. Each `files` entry SHALL carry `{ file, additions, deletions, diff, isBinary }`, with the full per-file patch joined under the `b/` path key.

#### Scenario: Head ref missing returns null

- **WHEN** `git rev-parse --verify refs/heads/<head>` exits non-zero
- **THEN** `GetDiff` returns `null` and issues no diff/merge-base queries

#### Scenario: merge-base failure falls back to baseBranch

- **WHEN** `git merge-base <baseBranch> <head>` exits non-zero but the diff and numstat succeed
- **THEN** the returned `mergeBase` equals `baseBranch`

#### Scenario: Per-file diff is keyed by the b/ path

- **WHEN** the full diff contains `diff --git a/foo.txt b/foo.txt` followed by patch lines
- **THEN** the matching `files` entry has `file: "foo.txt"` and `diff` containing the joined patch for that file

### Requirement: GetCommits returns the commit list with file totals

`GetCommits` SHALL issue, in parallel, log (`--format=%H\t%h\t%s\t%an\t%ad`, `--date=iso`), numstat, merge-base, and ahead/behind count against `<baseBranch>...<head>`, and return `{ base, head, mergeBase, ahead, behind, filesChanged, totalAdditions, totalDeletions, commits }`. Each commit SHALL be `{ hash, shortHash, message, author, date, files: [] }`. `mergeBase` SHALL fall back to `baseBranch` on a non-zero exit.

#### Scenario: Commits are parsed from the tab-separated log

- **WHEN** the log emits `<hash>\t<shortHash>\t<subject>\t<author>\t<date>` lines
- **THEN** `commits` contains one entry per non-empty line in the same field order with `files: []`

#### Scenario: merge-base failure falls back to baseBranch

- **WHEN** `git merge-base` exits non-zero
- **THEN** the returned `mergeBase` equals `baseBranch`

### Requirement: GetCommitDiff returns the raw patch for one commit

`GetCommitDiff` SHALL run `git show --format= --patch <hash>` against the resolved workDir and return `{ diff: <stdout> }` when the exit code is `0`. A non-zero exit MUST return `null`.

#### Scenario: Successful show returns the patch

- **WHEN** `git show --format= --patch <hash>` exits `0` with patch stdout
- **THEN** the handler returns `{ diff: <stdout> }`

#### Scenario: Non-zero exit returns null

- **WHEN** `git show --format= --patch <hash>` exits non-zero (e.g. unknown hash)
- **THEN** the handler returns `null`

### Requirement: GetWorkspaceStatus reports existence, rebase state, and origin ahead/behind

`GetWorkspaceStatus` SHALL return `{ exists: false }` when the workspace is unresolvable, the worktree probe fails, or the head ref does not exist (`git rev-parse --verify refs/heads/<head>` non-zero). When the workspace exists it SHALL build a `baseStatus` of `{ exists: true, branch: <head>, baseBranch: <baseBranch>, rebaseInProgress, conflictingFiles }`, where `rebaseInProgress` is true iff `git rebase --show-current-patch` exits `0`, and `conflictingFiles` is the non-empty output of `git diff --name-only --diff-filter=U` while a rebase is in progress (empty array otherwise). It SHALL then run `git fetch origin <baseBranch>`; a failed fetch MUST return `{ ...baseStatus, reason: "fetch_failed" }`. An in-progress rebase MUST return `{ ...baseStatus, reason: "rebase_in_progress" }` without computing ahead/behind. Otherwise it SHALL compute ahead/behind from `origin/<baseBranch>...<head>` and return `{ ...baseStatus, ahead, behind }`.

#### Scenario: Failed fetch returns the base status with reason fetch_failed

- **WHEN** the workspace exists and a rebase is in progress but `git fetch origin <baseBranch>` exits non-zero
- **THEN** the handler returns `{ ...baseStatus, reason: "fetch_failed" }` and does NOT run `rev-list --left-right --count origin/...`

#### Scenario: In-progress rebase returns reason rebase_in_progress after a successful fetch

- **WHEN** the workspace exists, a rebase is in progress, and `git fetch origin <baseBranch>` succeeds
- **THEN** the handler returns `{ ...baseStatus, reason: "rebase_in_progress" }` and does NOT compute ahead/behind

#### Scenario: Healthy workspace returns origin-relative ahead/behind

- **WHEN** the workspace exists, no rebase is in progress, and the fetch succeeds
- **THEN** the handler returns `{ ...baseStatus, ahead, behind }` derived from `git rev-list --left-right --count origin/<baseBranch>...<head>` (left count = behind, right count = ahead)

#### Scenario: Conflicting files are listed only while rebasing

- **WHEN** `git rebase --show-current-patch` exits `0` and `git diff --name-only --diff-filter=U` lists paths
- **THEN** `conflictingFiles` contains those paths; otherwise `conflictingFiles` is `[]`

### Requirement: GetFileContent returns base and head contents independently

`GetFileContent` SHALL run `git show <baseBranch>:<path>` and `git show <head>:<path>` in parallel and return `{ base, head }` where each side is the command's stdout when the exit is `0` and `null` otherwise. The two sides SHALL be resolved independently so a missing base returns `null` while a present head still returns its content, and vice versa.

#### Scenario: Both sides present

- **WHEN** both `git show` invocations exit `0`
- **THEN** the handler returns `{ base: <baseStdout>, head: <headStdout> }`

#### Scenario: Base missing, head present

- **WHEN** the base `git show` exits non-zero and the head exits `0`
- **THEN** the handler returns `{ base: null, head: <headStdout> }`

#### Scenario: Both missing

- **WHEN** both `git show` invocations exit non-zero
- **THEN** the handler returns `{ base: null, head: null }`

### Requirement: Numstat parsers tolerate binary entries and malformed lines

The numstat-driven parsers (`parseDiffFiles`, `parseNumstatTotal`) SHALL skip blank lines and lines with fewer than three tab-separated fields. A line whose first two fields are both `-` SHALL be treated as binary: `additions` and `deletions` SHALL both be `0` and `isBinary` SHALL be `true` in `parseDiffFiles`. Non-binary additions/deletions SHALL be parsed with `parseInt` defaulting to `0` on parse failure. `parseNumstatTotal` SHALL aggregate `filesChanged`, `additions`, and `deletions` across all valid lines using the same binary rule.

#### Scenario: Binary file line yields zero additions and deletions

- **WHEN** a numstat line is `-\t-\tbin/logo.png`
- **THEN** the parsed file has `{ additions: 0, deletions: 0, isBinary: true }`

#### Scenario: Malformed numstat lines are skipped

- **WHEN** the numstat output contains blank lines or lines with fewer than three tab fields
- **THEN** those lines contribute no file and do not affect the totals

#### Scenario: parseNumstatTotal aggregates across files

- **WHEN** numstat contains `3\t1\ta.txt` and `-\t-\tb.bin`
- **THEN** `parseNumstatTotal` returns `{ filesChanged: 2, additions: 3, deletions: 1 }`

### Requirement: Ahead/behind and commit parsers tolerate malformed output

`parseAheadBehind` SHALL parse a single tab-separated `<behind>\t<ahead>` line into `[ahead, behind]`, returning `[0, 0]` when the line does not split into exactly two fields or the integers fail to parse. `parseCommits` SHALL return an empty array on empty/whitespace-only input and SHALL drop log lines with fewer than five tab-separated fields.

#### Scenario: parseAheadBehind on well-formed output

- **WHEN** the rev-list output is `3\t2\n`
- **THEN** `parseAheadBehind` returns `[2, 3]` (ahead=2, behind=3)

#### Scenario: parseAheadBehind on malformed output

- **WHEN** the rev-list output is empty or a single field
- **THEN** `parseAheadBehind` returns `[0, 0]`

#### Scenario: parseCommits drops short lines

- **WHEN** the log output contains a line with fewer than five tab fields
- **THEN** that line is excluded from the parsed commits array
