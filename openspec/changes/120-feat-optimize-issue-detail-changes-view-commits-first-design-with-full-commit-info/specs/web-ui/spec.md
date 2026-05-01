## MODIFIED Requirements

### Requirement: Changes section defaults to Commits tab

The Changes section in IssueDetailPage SHALL initialize `diffTab` state to `'commits'` instead of `'files'`. The Files tab remains accessible via tab switching.

#### Scenario: Changes section renders with Commits active

- **WHEN** user navigates to an issue detail page that has changes
- **THEN** the Changes section renders with the Commits tab as the active view
- **AND** the Commits tab button shows selected/active styling

### Requirement: Commit rows use DiffViewer for expanded diff

When a commit row is expanded in the Commits tab, the diff content SHALL be rendered using the existing `DiffViewer` component. The inline `CommitDiffView` component SHALL be removed from IssueDetailPage.

#### Scenario: Expand commit to view diff with DiffViewer

- **WHEN** user clicks a commit row to expand it
- **THEN** the diff is rendered using `DiffViewer` component
- **AND** line numbers are displayed in old/new columns
- **AND** file blocks are independently expandable/collapsible

### Requirement: Files tab displays inline diff with DiffViewer

The Files tab SHALL allow clicking a file row to expand and display the inline diff using `DiffViewer`. The diff content for each file SHALL come from the `diff` field in the enhanced diff API response.

#### Scenario: Click file to expand diff

- **WHEN** user clicks a file row in the Files tab
- **THEN** the file row expands to show the full diff for that file using `DiffViewer`
- **AND** additions and deletions counts use precise values from the API

### Requirement: Frontend types include files field on CommitEntry

The `CommitEntry` interface SHALL include a `files: string[]` field. The `DiffFile` interface SHALL include `diff: string` and `isBinary: boolean` fields.

#### Scenario: CommitEntry type has files

- **WHEN** the TypeScript interface `CommitEntry` is inspected
- **THEN** it includes `files: string[]` alongside existing fields

#### Scenario: DiffFile type has diff and isBinary

- **WHEN** the TypeScript interface `DiffFile` is inspected
- **THEN** it includes `diff: string` and `isBinary: boolean` alongside existing fields
