## ADDED Requirements

### Requirement: Issue review surface follows worktree lifecycle

Mohist SHALL provide a PR-like review surface for an issue while that issue's worktree exists. When the worktree has been removed, the review surface SHALL clearly explain that diff and commit data are unavailable by lifecycle design and SHALL NOT imply that the issue produced no changes.

#### Scenario: Worktree exists

- **WHEN** an issue worktree exists
- **THEN** the review surface shows base/head branch metadata
- **AND** shows files changed, commit count, additions, and deletions summary
- **AND** provides Files changed and Commits views for the current base-to-head range

#### Scenario: Worktree removed

- **WHEN** an issue worktree has been removed after the issue lifecycle ended
- **THEN** the review surface shows a workspace-removed unavailable state
- **AND** the unavailable copy explains that diff and commits are only available while the issue worktree is retained
- **AND** the surface does not show `No changes yet` or `No commits yet`

#### Scenario: Issue not started

- **WHEN** an issue has not started and has no worktree
- **THEN** the review surface may show `No changes yet`
- **AND** it does not describe the workspace as removed

### Requirement: Files changed is the primary review view

The review surface SHALL make Files changed the default and primary view. It SHALL show the complete changed file list and allow users to expand file diffs with line-numbered patch rendering.

#### Scenario: Open issue with file changes

- **WHEN** a user opens an issue whose retained worktree has file changes relative to base
- **THEN** Files changed is selected by default
- **AND** every changed file is listed with additions and deletions
- **AND** expanding a file shows its unified diff with line numbers

#### Scenario: No file changes in retained worktree

- **WHEN** an issue worktree exists but there are no file changes relative to base
- **THEN** the Files changed view shows `No file changes yet`

### Requirement: Commits view shows complete change narrative

The review surface SHALL provide a Commits companion view that shows the complete base-to-head commit range. Each commit SHALL show identifying metadata, touched files, summary stats, and an expandable patch diff.

#### Scenario: Multi-commit issue branch

- **WHEN** an issue branch contains multiple commits relative to base
- **THEN** the Commits view lists every commit in the range
- **AND** no commit is dropped because of git log/stat parsing

#### Scenario: Commit row metadata

- **WHEN** commits are shown
- **THEN** each commit row includes hash, message, author or time, files touched, additions, and deletions

#### Scenario: Expand commit patch

- **WHEN** a user expands a commit
- **THEN** the commit patch is loaded lazily
- **AND** the patch is rendered with the existing line-numbered diff viewer
