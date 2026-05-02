## ADDED Requirements

### Requirement: Changes panel displays summary statistics header

The Changes panel SHALL display a summary header at the top showing the total number of changed files, total additions, total deletions, and total commit count. The header SHALL be computed from the `getIssueDiff` and `getIssueCommits` API responses without requiring new endpoints.

#### Scenario: Summary header with changes present

- **WHEN** the issue has 5 changed files, 120 additions, 45 deletions, and 3 commits
- **THEN** the Changes panel header displays "5 files · +120 −45 · 3 commits"
- **AND** additions are shown in green text, deletions in red text

#### Scenario: Summary header with no changes

- **WHEN** the issue has no diff data and no commits
- **THEN** the Changes panel displays an empty state message "No changes yet"
- **AND** no summary statistics header is shown

#### Scenario: Summary header with files but no commits

- **WHEN** the issue has 2 changed files but 0 commits
- **THEN** the summary header displays "2 files · +N −M" without a commit count segment

#### Scenario: Summary statistics update on data refresh

- **WHEN** the agent makes new changes during a running session
- **AND** the diff/commits data refreshes via React Query
- **THEN** the summary statistics update to reflect the latest data
