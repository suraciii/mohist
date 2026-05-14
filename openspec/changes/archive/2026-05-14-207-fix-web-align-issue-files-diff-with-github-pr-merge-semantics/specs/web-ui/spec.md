## MODIFIED Requirements

### Requirement: Issue Detail shows merge summary and issue commits

Issue Detail SHALL keep a lightweight changes summary and provide a `View files` entry into the dedicated changed-files reader, but the summary SHALL now describe merge intent before diff counts. Issue Detail SHALL also show a lightweight commits section so users can understand which commits compose the issue's pending merge content.

#### Scenario: View files from Issue Detail

- **WHEN** a user opens Issue Detail for an issue with available change data
- **THEN** the page shows merge framing such as `head wants to merge into base`
- **AND** shows files changed, additions, deletions, and merge-base diff context
- **AND** provides a `View files` action that navigates to `/issue/:number/files`

#### Scenario: Issue Detail commit summary

- **WHEN** a user opens Issue Detail for an issue with available commit data
- **THEN** the page shows a commits section with commit count and a list of recent issue commits
- **AND** each commit item can navigate to commit-specific inspection in the changed-files reader

#### Scenario: Issue Detail remains lightweight

- **WHEN** a user is browsing Issue Detail
- **THEN** the page keeps merge and commit context visible
- **AND** it does not embed a full changed-files diff review experience inline

### Requirement: Changed-files page preserves reading context across navigation

The Web UI SHALL preserve enough client-side state for users to resume reading when they return to the changed-files page for the same issue. Restored context SHALL include the user's reading position or equivalent file/hunk anchor and the active diff mode when available.

#### Scenario: Return to prior reading position

- **WHEN** a user navigates away from `/issue/:number/files` and later returns to the same issue
- **THEN** the page restores the user's prior reading context for that issue
- **AND** the user does not need to manually relocate the previously active file or hunk from the top of the page
