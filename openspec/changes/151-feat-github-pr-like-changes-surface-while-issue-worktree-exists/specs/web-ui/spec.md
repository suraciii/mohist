## MODIFIED Requirements

### Requirement: Issue Detail presents review summary

Issue Detail SHALL surface review metadata near the top of the issue context while review data is available. The summary SHALL show the issue stage/status context together with base branch, head branch, files changed, commit count, additions, deletions, worktree availability, and merge truth when known.

#### Scenario: Retained worktree summary

- **WHEN** a user opens an issue with a retained worktree
- **THEN** Issue Detail shows base/head branch metadata
- **AND** shows files changed, commit count, additions, and deletions
- **AND** indicates that the worktree is retained
- **AND** shows the current merge state using existing merge truth

### Requirement: Changes panel renders PR-like review UI

The Changes panel SHALL render as the primary review evidence area in Issue Detail. It SHALL default to Files changed, provide a Commits companion tab, and render file and commit patches through the existing diff viewer.

#### Scenario: Files changed default

- **WHEN** a user opens an issue with available review data
- **THEN** the Changes panel defaults to Files changed
- **AND** the user can switch to Commits

#### Scenario: File diff expansion

- **WHEN** the user expands a changed file
- **THEN** the panel renders that file's unified diff with the existing diff viewer

#### Scenario: Commit diff expansion

- **WHEN** the user expands a commit
- **THEN** the panel lazily loads the commit patch
- **AND** renders it with the existing diff viewer

### Requirement: Changes panel explains empty and unavailable states

The Changes panel SHALL distinguish normal empty states from unavailable review data. It SHALL NOT use `No changes yet` or `No commits yet` when the worktree has been removed.

#### Scenario: Not started issue

- **WHEN** the issue has not started and has no worktree
- **THEN** the Changes panel shows `No changes yet`

#### Scenario: Removed worktree

- **WHEN** the worktree has been removed
- **THEN** the Changes panel shows `Changes unavailable`
- **AND** explains that diff and commits are only available while the issue worktree is retained

#### Scenario: Branch missing

- **WHEN** the review API reports `branch_missing`
- **THEN** the Changes panel shows a branch-missing unavailable state

#### Scenario: Git or API failure

- **WHEN** loading review data fails
- **THEN** the Changes panel shows a failed-to-load state rather than a no-changes state
