## ADDED Requirements

### Requirement: Digest zone renders recent issue history summary

The Dashboard `Digest` zone SHALL render a recent-history summary composed of three categories of issues, each limited to a fixed top-N count: recently **completed** issues, recently **failed** issues, and recently **archived** issues. Completed and failed issues SHALL be derived from the active issue set ordered by `updatedAt`; archived issues SHALL be derived from the archived issue set ordered by `archivedAt`. Each summary row SHALL display the issue number, title, and a relative timestamp (for example "2h ago", "3d ago"). The top-N count SHALL be a fixed constant and SHALL NOT be user-configurable.

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
- **THEN** the rows SHALL be ordered by their respective timestamp (`updatedAt` for completed/failed, `archivedAt` for archived) with the most recent first

### Requirement: Digest rows navigate to issue detail

Each Digest summary row SHALL be jumpable. Activating a row SHALL navigate the user to the corresponding issue detail view. The navigation target SHALL be the same issue detail surface reachable elsewhere in the application.

#### Scenario: Activating a completed issue row opens its detail

- **WHEN** a user activates a completed issue row in the Digest zone
- **THEN** the application SHALL navigate to that issue's detail view
- **AND** the issue detail SHALL correspond to the issue represented by the row

#### Scenario: Activating an archived issue row opens its detail

- **WHEN** a user activates an archived issue row in the Digest zone
- **THEN** the application SHALL navigate to that archived issue's detail view

### Requirement: Digest zone renders empty state when no recent history exists

When there are no recent completed, failed, or archived issues to summarize, the `Digest` zone SHALL render an empty-state message indicating there is no recent activity. The empty state SHALL NOT render empty category lists or loading artifacts once data has resolved.

#### Scenario: No recent history shows empty state

- **WHEN** the Digest zone data has resolved
- **AND** there are no recent completed, failed, or archived issues
- **THEN** the zone SHALL render an empty-state message
- **AND** the zone SHALL NOT render empty category lists

#### Scenario: Empty state is distinct from loading

- **WHEN** the underlying issue queries are still loading
- **THEN** the zone SHALL render a loading indicator rather than the empty-state message
- **AND** the empty-state message SHALL only appear once data has resolved to empty

### Requirement: Digest zone derives content exclusively from existing read-only sources

The `Digest` zone SHALL derive all of its content from existing frontend data sources — the active issue query (`useIssues`), the archived issue query (`useArchivedIssues`), and the events-hub. The change SHALL NOT introduce any new backend API endpoint, SHALL NOT mutate issue or event domain state, and SHALL NOT add write operations.

#### Scenario: No new backend endpoint is introduced

- **WHEN** the `Digest` zone renders and refreshes its data
- **THEN** the zone SHALL consume only existing query and event sources
- **AND** no new backend API endpoint SHALL be added to support the Digest zone

#### Scenario: Digest is read-only with respect to domain state

- **WHEN** a user views the `Digest` zone
- **THEN** the zone SHALL NOT perform any write or mutation against issue, activity, or event domain state
- **AND** the zone SHALL be purely a read-only composition over existing data

### Requirement: Optional activity event summary shares the Activity page source

If an activity event summary is rendered within the `Digest` zone, it SHALL be sourced from the same events-hub that feeds the Activity page, limited to a fixed top-N count. The `Digest` zone SHALL NOT introduce event filtering, category filtering, or any search capability — those remain the responsibility of the Activity page. The Digest activity summary SHALL be a windowed overview, not a replacement for the Activity page event stream.

#### Scenario: Activity summary shares Activity page event source

- **WHEN** the `Digest` zone renders an activity event summary
- **THEN** the summary SHALL be sourced from the same events-hub consumed by the Activity page
- **AND** the summary SHALL be limited to a fixed top-N most recent events

#### Scenario: Digest does not provide event filtering

- **WHEN** the `Digest` zone renders an activity event summary
- **THEN** the zone SHALL NOT expose event category filters, search inputs, or filtering controls
- **AND** filtering and full event-stream browsing SHALL remain available only on the Activity page
