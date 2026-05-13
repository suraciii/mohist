## MODIFIED Requirements

### Requirement: Issue Detail changes summary links to the dedicated reader

Issue Detail SHALL keep a lightweight changes summary and provide a `View files` entry into the dedicated changed-files reader. The summary SHALL not require the user to expand inline per-file diffs in order to reach the primary file-reading experience.

#### Scenario: View files from Issue Detail

- **WHEN** a user opens Issue Detail for an issue with available change data
- **THEN** the page shows a lightweight changes summary with base/head and diffstat context
- **AND** it provides a `View files` action that navigates to `/issue/:number/files`

#### Scenario: Issue Detail remains lightweight

- **WHEN** a user is browsing Issue Detail
- **THEN** the page keeps changes context visible
- **AND** it does not require the user to stay inside issue description, comments, tasks, or session entry surfaces to perform the primary code-reading workflow

### Requirement: Changed-files page preserves reading context across navigation

The Web UI SHALL preserve enough client-side state for users to resume reading when they return to the changed-files page for the same issue. Restored context SHALL include the user's reading position or equivalent file/hunk anchor and the active diff mode when available.

#### Scenario: Return to prior reading position

- **WHEN** a user navigates away from `/issue/:number/files` and later returns to the same issue
- **THEN** the page restores the user's prior reading context for that issue
- **AND** the user does not need to manually relocate the previously active file or hunk from the top of the page
