## MODIFIED Requirements

### Requirement: REQ-WUI-198-002 Kanban board supports focused filtering and sorting

The Kanban board SHALL support priority filtering, label filtering, title search, and shared sort switching across all stage columns, with the board query state persisted in the URL. Label filtering SHALL operate on key-value labels governed by the `issue-labels` capability: a label filter represents a `key=value` pair, and an issue matches the filter when its label map contains that exact key-value pair. The label filter surface SHALL present labels in `key=value` form rather than as flat chips.

#### Scenario: Priority and label filters update board counts
- **WHEN** a user applies priority or label (`key=value`) filters
- **THEN** each stage column updates its issue list and displayed count to match the filtered set

#### Scenario: Label filter matches key-value pairs
- **WHEN** a user applies the label filter `stream=frontend`
- **AND** an issue's label map contains `{ "stream": "frontend" }`
- **THEN** that issue is included in the filtered board
- **AND** an issue whose label map contains `{ "stream": "backend" }` is excluded

#### Scenario: Search filters by title
- **WHEN** a user types into the board search box
- **THEN** issues are filtered in real time by title match

#### Scenario: Shared sort mode updates all columns
- **WHEN** a user switches sort mode to `updated`
- **THEN** every stage column reorders its issues using that same mode

#### Scenario: Board state is restored from URL
- **WHEN** a user refreshes the board or reopens a bookmarked filtered URL
- **THEN** priority filters, label (`key=value`) filters, search text, and sort mode are restored from the URL

#### Scenario: Mobile board uses the same focused view
- **WHEN** a user views the board on mobile
- **THEN** the single-column stage view reflects the same filtered and sorted issue set as desktop

### Requirement: REQ-WUI-209-003 Homepage label filtering reaches all labels

The homepage SHALL preserve the #198 URL-backed search, priority, label, and sort model while making all project label keys reachable from the label filter UI. The filter surface SHALL remain compact and SHALL NOT limit reachable labels to the first eight returned label keys. Label filters SHALL be expressed as `key=value` pairs governed by the `issue-labels` capability.

#### Scenario: Label beyond the first eight is selectable
- **WHEN** the project contains more than eight label keys
- **AND** a user wants to filter by a label that is not in the first eight visible labels
- **THEN** the homepage provides a way to discover and select that label
- **AND** the board updates using the same label-filter semantics as other labels

#### Scenario: Board state remains URL-backed
- **WHEN** a user applies search, priority, label, or sort controls on the homepage
- **THEN** the board state continues to be reflected in and restored from the URL

## ADDED Requirements

### Requirement: Issue Create/Edit label editor accepts key and value

The Web UI Issue Create and Edit dialogs SHALL provide a label editor that accepts a `key` and a `value` for each label pair, governed by the `issue-labels` capability. The editor SHALL NOT present labels as flat toggleable chips. Invalid keys and empty values SHALL be surfaced as clear errors before submission.

#### Scenario: Add a key-value label in Create Issue
- **WHEN** a user opens the Create Issue dialog and enters key `stream` and value `frontend`
- **THEN** the dialog submits the issue with a label map containing `{ "stream": "frontend" }`

#### Scenario: Edit an existing issue's labels by key
- **WHEN** a user opens the Edit Issue dialog for an issue whose labels are `{ "stream": "frontend" }`
- **AND** changes the value for key `stream` to `backend`
- **THEN** the dialog submits an update whose label map contains `{ "stream": "backend" }`

#### Scenario: Invalid label key is blocked before submit
- **WHEN** a user enters an uppercase or whitespace key in the label editor
- **THEN** the dialog shows a clear validation error
- **AND** the submit is blocked until the key is corrected
