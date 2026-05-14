## MODIFIED Requirements

### Requirement: Issue review surfaces stay semantically consistent across branches

Mohist SHALL describe an issue's pending merge content consistently across Issue Detail and the dedicated changed-files page. When the issue branch is behind base, the review surfaces SHALL continue to show only the issue's merge-base contribution and SHALL NOT imply that base-only changes belong to the issue.

#### Scenario: Consistent counts across surfaces

- **WHEN** Issue Detail and `/issue/:number/files` both show available review data for the same issue
- **THEN** both surfaces use the same merge-base diff summary
- **AND** they do not disagree on file counts because one used two-dot diff and the other used merge-base diff

#### Scenario: Behind-base branch review

- **WHEN** the issue branch is ahead and behind base at the same time
- **THEN** the review surfaces show only changes introduced by the issue branch from the merge base
- **AND** the visible file set does not include base-only changes

### Requirement: Issue Detail exposes issue commit history as review context

Issue Detail SHALL present commit history as a first-class review context for understanding what work composes the pending merge. The commit list SHALL reflect the same pending merge narrative as Files changed while remaining a lightweight navigation surface rather than a full inline diff reviewer.

#### Scenario: Issue commit list shown on detail page

- **WHEN** an issue has available review data and one or more commits relative to base
- **THEN** Issue Detail shows a commits section with commit count, short hash, subject, and author/time or equivalent metadata

#### Scenario: Commit navigation

- **WHEN** a user activates a commit item from Issue Detail
- **THEN** the user can navigate to commit-specific inspection in the changed-files reader
- **AND** the default issue-level Files changed semantic remains unchanged

#### Scenario: Commit section unavailable state

- **WHEN** commit history is unavailable because the issue has not started, the worktree has been removed, the branch is missing, or git fails
- **THEN** Issue Detail shows a clear empty or unavailable state for the commits section
- **AND** it does not silently omit the section in a way that suggests there were no commits to inspect
