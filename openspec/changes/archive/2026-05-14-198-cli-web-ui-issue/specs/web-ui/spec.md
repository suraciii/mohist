## MODIFIED Requirements

### Requirement: REQ-WUI-198-001 Web issue dialogs support priority editing

The Web UI SHALL expose issue priority in both create and edit dialogs using the same `p0`-`p4` semantics as the CLI.

#### Scenario: Create issue with priority
- **WHEN** a user opens Create Issue
- **THEN** the dialog shows a priority selector with `p0` through `p4`
- **AND** the default selection is `p2`

#### Scenario: Edit issue priority
- **WHEN** a user opens Edit Issue for an existing issue
- **THEN** the dialog shows the issue's current priority
- **AND** saving can update that priority through the issue API

### Requirement: REQ-WUI-198-002 Kanban board supports focused filtering and sorting

The Kanban board SHALL support priority filtering, label filtering, title search, and shared sort switching across all stage columns, with the board query state persisted in the URL.

#### Scenario: Priority and label filters update board counts
- **WHEN** a user applies priority or label filters
- **THEN** each stage column updates its issue list and displayed count to match the filtered set

#### Scenario: Search filters by title
- **WHEN** a user types into the board search box
- **THEN** issues are filtered in real time by title match

#### Scenario: Shared sort mode updates all columns
- **WHEN** a user switches sort mode to `updated`
- **THEN** every stage column reorders its issues using that same mode

#### Scenario: Board state is restored from URL
- **WHEN** a user refreshes the board or reopens a bookmarked filtered URL
- **THEN** priority filters, label filters, search text, and sort mode are restored from the URL

#### Scenario: Mobile board uses the same focused view
- **WHEN** a user views the board on mobile
- **THEN** the single-column stage view reflects the same filtered and sorted issue set as desktop
