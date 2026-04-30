## ADDED Requirements

### Requirement: Archived issues list page at /archived

The system SHALL provide a `/archived` route displaying all archived issues in a vertical list view, sorted by archived time descending.

#### Scenario: Navigate to archived page

- **WHEN** user navigates to `/archived`
- **THEN** the page displays all archived issues for the current project
- **AND** issues are sorted by `archivedAt` descending (most recently archived first)

#### Scenario: No archived issues

- **WHEN** user navigates to `/archived`
- **AND** there are no archived issues
- **THEN** the page displays an empty state message (e.g. "No archived issues")

### Requirement: Archived issue list item display

Each archived issue item SHALL display the issue number, title, completion time (relative format), archived time (relative format), and labels. Each item SHALL be clickable to navigate to the issue detail page.

#### Scenario: Archived issue item layout

- **WHEN** an archived issue is rendered in the list
- **THEN** it displays the issue number (e.g. `#42`)
- **AND** displays the issue title
- **AND** displays the completion time (relative format derived from `updatedAt` or `doneAt`)
- **AND** displays the archived time (relative format derived from `archivedAt`, e.g. "2 hours ago")
- **AND** displays the issue labels with existing label styling
- **AND** the entire item is clickable and links to `/issue/:number`

#### Scenario: Labels display

- **WHEN** an archived issue has labels
- **THEN** labels are rendered using the same `getLabelStyle` / `getStripColor` conventions as kanban cards

### Requirement: Restore button on archived issue items

Each archived issue item SHALL have a "恢复" (restore) button that unarchives the issue and removes it from the list.

#### Scenario: Click restore button

- **WHEN** user clicks "恢复" on an archived issue item
- **THEN** `POST /api/issues/:number/unarchive` is called
- **AND** the issue is removed from the archived list
- **AND** the issue reappears in the kanban Done column on next refresh

#### Scenario: Restore button during request

- **WHEN** the unarchive API request is in flight
- **THEN** the restore button shows a loading/disabled state
- **AND** prevents duplicate clicks

### Requirement: Search archived issues by title

The archived list page SHALL provide a search input that filters archived issues by title in real-time.

#### Scenario: Search by title

- **WHEN** user types text in the search input
- **THEN** the archived issue list is filtered client-side to only show issues whose title contains the search text (case-insensitive)

#### Scenario: Clear search

- **WHEN** user clears the search input
- **THEN** all archived issues are shown again

### Requirement: Back navigation from archived page

The archived list page SHALL provide a back button or link that returns to the kanban board.

#### Scenario: Click back button

- **WHEN** user clicks the back navigation element
- **THEN** browser navigates to `/` (kanban board)

### Requirement: Archived page styling consistency

The archived list page SHALL follow the existing design language of the application.

#### Scenario: Styling

- **WHEN** the archived page is rendered
- **THEN** it uses white card backgrounds with `shadow-sm`, gray-100/60 backgrounds, and typography consistent with the issue detail page
