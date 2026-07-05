### Requirement: Epic list supports title search

The epic list query (`EpicQuerier.ListAsync`) SHALL accept an optional title search term that filters epics to those whose title contains the term as a case-insensitive substring. The list route SHALL forward the search term as a query-string parameter. When no search term is provided, all epics in the project SHALL be returned (no filtering), preserving current behavior.

#### Scenario: Search filters by title substring

- **WHEN** the list is queried with a search term "auth"
- **THEN** only epics whose title contains "auth" (case-insensitive) SHALL be returned

#### Scenario: Empty search returns all epics

- **WHEN** the list is queried with no search term
- **THEN** all epics in the project SHALL be returned
- **AND** no title filtering SHALL be applied

### Requirement: Epic list supports sorting by priority or updated time

The epic list query SHALL accept a sort selector choosing the primary sort field (`priority` or `updated-at`) and a direction (`asc` or `desc`). The list route SHALL forward the sort field and direction as query-string parameters. The default ordering when no sort parameters are provided SHALL be priority ascending then updated-at descending (the current hardcoded behavior), so existing consumers see no change.

#### Scenario: Sort by priority ascending

- **WHEN** the list is queried with sort=priority and dir=asc
- **THEN** epics SHALL be ordered by priority ascending (p0 before p4)

#### Scenario: Sort by updated-at descending

- **WHEN** the list is queried with sort=updated and dir=desc
- **THEN** epics SHALL be ordered by updated-at descending (most recently updated first)

#### Scenario: Default ordering is unchanged

- **WHEN** the list is queried with no sort parameters
- **THEN** epics SHALL be ordered by priority ascending then updated-at descending

### Requirement: Search and sort compose

A request MAY combine a title search term with a sort field and direction simultaneously. The search filter and the sort SHALL apply together without interfering: the filtered set SHALL be ordered according to the requested sort.

#### Scenario: Search combined with sort

- **WHEN** the list is queried with both a search term and a sort field/direction
- **THEN** only matching epics SHALL be returned
- **AND** the matching epics SHALL be ordered per the requested sort

### Requirement: Web list page search and sort controls

The web epic list page SHALL render a search input that drives the title search and a sort control that selects the sort field and direction. Changing the search input or sort control SHALL update the list query. The status-grouped presentation (running, ready-to-start, waiting/blocked, idle/empty, paused, done, closed) SHALL continue to group the resulting — optionally filtered and sorted — epic set.

#### Scenario: Typing in the search box filters the list

- **WHEN** a user types a term into the list-page search box
- **THEN** the list SHALL filter to epics whose title matches the term

#### Scenario: Changing the sort control reorders the list

- **WHEN** a user changes the sort field or direction via the sort control
- **THEN** the list SHALL reorder according to the selected sort
