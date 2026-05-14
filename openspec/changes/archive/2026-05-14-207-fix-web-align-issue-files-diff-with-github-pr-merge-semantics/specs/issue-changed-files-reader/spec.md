## MODIFIED Requirements

### Requirement: Changed-files page uses merge-base reading semantics

Mohist SHALL provide a dedicated issue changed-files page at `/issue/:number/files` for reading the change set the issue branch would merge into base. The page SHALL frame the relationship as head merging into base and SHALL show merge-base-to-head changes rather than a generic base-vs-head two-dot diff.

#### Scenario: Open changed-files page

- **WHEN** a user opens `/issue/:number/files` for an issue with available diff data
- **THEN** the page shows the issue number and title
- **AND** shows merge-framed base/head metadata together with files changed, additions, deletions, and stage/status context
- **AND** explicitly indicates it is showing merge-base to issue-head changes

#### Scenario: Behind-base notice

- **WHEN** the issue branch is behind base
- **THEN** the page shows a non-blocking notice explaining that Files changed displays only changes introduced by the issue from the merge base
- **AND** the notice does not block reading the diff

### Requirement: Changed-files page defaults to continuous file reading

The changed-files page SHALL present changed files in a directory-grouped tree and SHALL render the file diff stream as the primary reading flow. Users SHALL NOT need to select a file before any diff content is visible.

#### Scenario: Continuous diff flow

- **WHEN** a user opens the changed-files page with available file diffs
- **THEN** diff content is visible in the main reader by default
- **AND** the left tree is used for locating and jumping between changed files rather than gating first visibility of patches

#### Scenario: Filter files by path

- **WHEN** a user enters part of a file path in the file filter input
- **THEN** the tree narrows to matching files
- **AND** the visible reading flow can be navigated from the filtered tree

### Requirement: Advanced diff modes remain secondary controls

The changed-files page SHALL keep advanced reading features such as split diff, raw patch, full-file view, diff search, and commit-scoped reading, but these SHALL be exposed as secondary reading controls rather than first-class peers to the primary Files changed workflow.

#### Scenario: Toolbar emphasizes reading aids

- **WHEN** a user reads the changed-files page toolbar
- **THEN** the primary controls focus on all-commits scope, file filtering, and diff settings
- **AND** advanced per-file or alternate reading modes are available through secondary controls

#### Scenario: Commit-scoped reading remains available

- **WHEN** a user chooses to inspect a specific commit from the changed-files page
- **THEN** the reader can switch to that commit's file changes within the same reading surface
- **AND** the page clearly distinguishes commit-specific inspection from the default merge-base issue diff
