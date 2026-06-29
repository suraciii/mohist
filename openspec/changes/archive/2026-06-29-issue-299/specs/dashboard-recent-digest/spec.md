## MODIFIED Requirements

### Requirement: Digest zone renders recent issue history summary

The Dashboard `Digest` zone SHALL render a recent-history summary composed of three categories of issues, each limited to a fixed top-N count: recently **completed** issues, recently **failed** issues, and recently **archived** issues. Completed issues SHALL be derived from the active issue set ordered by `completedAt` (the issue's persisted completion time); failed issues SHALL be derived from the active issue set ordered by `updatedAt`; archived issues SHALL be derived from the archived issue set ordered by `archivedAt`. Each summary row SHALL display the issue number, title, and a relative timestamp (for example "2h ago", "3d ago"). The top-N count SHALL be a fixed constant and SHALL NOT be user-configurable.

#### Scenario: All three categories render with recent issues

- **WHEN** the Dashboard `Digest` zone renders for a project that has recently completed, failed, and archived issues
- **THEN** the zone SHALL render a completed section listing up to top-N recently completed issues
- **AND** the zone SHALL render a failed section listing up to top-N recently failed issues
- **AND** the zone SHALL render an archived section listing up to top-N recently archived issues
- **AND** each row SHALL display the issue number, title, and a relative timestamp

#### Scenario: Each category is capped at a fixed top-N count

- **WHEN** a category contains more than top-N recent issues
- **THEN** the zone SHALL render only the top-N most recent rows for that category
- **AND** the top-N count SHALL NOT be configurable by the user

#### Scenario: Category with no recent issues is omitted or shows inner empty hint

- **WHEN** a category (completed, failed, or archived) has no recent issues
- **THEN** the zone SHALL either omit that category section or render an inline empty hint for that category
- **AND** the remaining non-empty categories SHALL still render

#### Scenario: Rows are ordered by most recent first

- **WHEN** the Digest zone renders a category with multiple issues
- **THEN** the completed category rows SHALL be ordered by `completedAt` with the most recent first
- **AND** the failed category rows SHALL be ordered by `updatedAt` with the most recent first
- **AND** the archived category rows SHALL be ordered by `archivedAt` with the most recent first

#### Scenario: Editing a completed issue does not resurface it in recently completed

- **WHEN** a `done` issue was completed on a prior day
- **AND** the issue's `updatedAt` is bumped to the current day by a post-completion edit
- **THEN** the issue SHALL NOT jump to the top of the recently completed list
- **AND** the recently completed list ordering SHALL be driven by `completedAt`, not by `updatedAt`
