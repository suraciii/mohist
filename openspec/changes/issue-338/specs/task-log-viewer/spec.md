### Requirement: Keyword search filters log lines by case-insensitive substring

The task log panel SHALL provide a search input. As the user types, the panel SHALL filter the loaded lines in real time to only those whose `text` or `source` contains the typed term as a case-insensitive substring. The search SHALL be a pure client-side computation (e.g. `useMemo`) over the lines already held in the React Query cache; no server request SHALL be issued for search. Matching SHALL NOT depend on any `level` field, because task-log lines carry no `level`.

#### Scenario: Typing a keyword narrows the visible lines

- **WHEN** the loaded lines contain texts `Cloning repo`, `CONFLICT (content)`, and `Patch failed` and the user types `conflict` into the search input
- **THEN** only the line whose `text` contains `CONFLICT` SHALL be visible
- **AND** no new network request SHALL be made

#### Scenario: Search matches against source as well as text

- **WHEN** the user types a token that appears only in a line's `source` (e.g. `rebase`) and in no line's `text`
- **THEN** every line whose `source` contains that token SHALL remain visible

#### Scenario: Search is case-insensitive

- **WHEN** a line's `text` is `Rebase succeeded` and the user types `REBASE`
- **THEN** that line SHALL remain visible

### Requirement: Source-chip filters narrow lines by phase origin and are the sole filter dimension

The panel SHALL render one chip per distinct `source` value present in the loaded lines (e.g. `workspace-prep`, `branch-check`, `action:rebase`, `cleanup`). The chip set SHALL be data-driven from the loaded lines, not a fixed enum. Chips SHALL be multi-select; toggling a chip SHALL include or exclude that source. There SHALL be intentionally no level/error/warning severity dimension, because task-log lines have no `level` field (Phase 1 merged stdout/stderr and dropped level).

#### Scenario: Chips are derived from the distinct sources present

- **WHEN** the loaded lines carry sources `workspace-prep`, `action:rebase`, and `cleanup`
- **THEN** the panel SHALL render exactly one chip for each of those three sources
- **AND** no chip for a source absent from the loaded lines SHALL be rendered

#### Scenario: Toggling a chip hides that source's lines

- **WHEN** the user toggles off the `action:rebase` chip
- **THEN** no line whose `source` is `action:rebase` SHALL be visible
- **AND** lines of other sources SHALL remain visible

#### Scenario: No severity dimension is offered

- **WHEN** the filter controls are inspected
- **THEN** no control keyed on error/warning/info severity SHALL exist

### Requirement: Search and source filtering compose

A line SHALL be visible only if it satisfies BOTH the current search term AND the enabled source chips. Failing either criterion SHALL hide the line.

#### Scenario: A line must pass both filters

- **WHEN** the search term is `error` and only the `action:rebase` chip is enabled
- **THEN** only lines whose `source` is `action:rebase` AND whose `text` or `source` contains `error` SHALL be visible

### Requirement: Download exports the currently filtered view as a text file

The panel SHALL provide a download button that exports the currently filtered lines (the result of the active search and source filtering) as a `.txt` file via a client-side `Blob` and a temporary `<a download>` click, with no server round-trip. The exported content SHALL include one entry per visible log line in seq order, preserving each line's `text`. The filename SHALL match `task-logs-<taskId>-YYYY-MM-DD.txt`.

#### Scenario: Download reflects the current filter

- **WHEN** the user applies a search or source filter and clicks download
- **THEN** the produced file SHALL contain exactly the currently visible (filtered) lines in order
- **AND** lines hidden by the active filter SHALL NOT appear in the file

#### Scenario: Download with no filter equals the whole log

- **WHEN** the search term is empty, all source chips are enabled, and the user clicks download
- **THEN** the file SHALL contain every loaded line

#### Scenario: Download is client-only

- **WHEN** the download button is clicked
- **THEN** no HTTP request to a server endpoint SHALL be triggered to produce the export

#### Scenario: Filename follows the convention

- **WHEN** the download is triggered for taskId `build-task-1`
- **THEN** the offered filename SHALL match the pattern `task-logs-build-task-1-YYYY-MM-DD.txt`

### Requirement: Default filter state shows the full log

On open, the search box SHALL be empty and all source chips SHALL be enabled, so the full loaded log is visible before the user interacts.

#### Scenario: Fresh panel shows everything

- **WHEN** the panel renders with loaded lines
- **THEN** the search input SHALL be empty
- **AND** every source chip SHALL be in the enabled state
- **AND** every loaded line SHALL be visible

### Requirement: Boundary states have distinct actionable messages

The panel SHALL render distinct messages for: an empty log (no lines loaded), a non-empty log with no search matches, and a non-empty log with no source-chip matches. Each message SHALL be user-readable and SHALL distinguish which condition applies.

#### Scenario: Empty log

- **WHEN** the loaded log has zero lines and no filter is active
- **THEN** the panel SHALL show a message indicating no execution log was captured

#### Scenario: No search matches

- **WHEN** the loaded log is non-empty and the search term matches no line
- **THEN** the panel SHALL show a message indicating no lines match the search

#### Scenario: No source-chip matches

- **WHEN** the loaded log is non-empty and the enabled source chips exclude every line
- **THEN** the panel SHALL show a message indicating no lines match the active source filters

### Requirement: Scroll-aware auto-follow pauses away from the bottom and resumes near it

The panel SHALL replace its current always-scroll-to-bottom behavior with scroll-aware auto-follow: when the user scrolls away from the bottom, auto-follow SHALL pause so new or filtered lines do not yank the viewport; when the user scrolls back near the bottom, auto-follow SHALL resume. This SHALL apply to both filtered and streaming views, mirroring the system Logs page interaction.

#### Scenario: Scrolling away pauses auto-follow

- **WHEN** new lines arrive or the filter changes while the user is scrolled away from the bottom
- **THEN** the panel SHALL NOT force the viewport back to the bottom

#### Scenario: Scrolling back near the bottom resumes auto-follow

- **WHEN** the user scrolls back to near the bottom after pausing
- **THEN** auto-follow SHALL resume and subsequent new lines SHALL scroll into view

### Requirement: Visual and interaction parity with the system Logs page

The search input (search icon and placeholder), source-chip styling, and download-button placement SHALL match the interaction pattern of the system Logs page so the two log surfaces feel like one product.

#### Scenario: Controls mirror the Logs page conventions

- **WHEN** the task log panel controls are inspected
- **THEN** the search input SHALL present a search icon and a placeholder
- **AND** the source chips SHALL use chip styling consistent with the Logs page level chips
- **AND** the download button SHALL be placed alongside the controls in the panel header region

### Requirement: New controls are accessible

The search input, each source chip, and the download button SHALL be keyboard reachable and SHALL expose appropriate accessible names and roles (e.g. a labeled input and named buttons) so the structural axe rules used across the app pass for the panel.

#### Scenario: Keyboard reachability

- **WHEN** a keyboard user tabs through the rendered panel
- **THEN** the search input, each source chip, and the download button SHALL each receive focus in turn

#### Scenario: Structural axe rules pass

- **WHEN** the panel with search, source chips, and download is rendered under the a11y test harness
- **THEN** it SHALL pass the same structural axe rules applied to the rest of the app

### Requirement: Search, filter, and download operate client-only with no backend change

Search, source filtering, and download SHALL operate entirely over the lines Phase 1/2 already pulled into the React Query cache (REST snapshot plus live-appended delta). No new server endpoint, query parameter, or wire type SHALL be introduced. The `{ seq, timestamp, source, text }` line model, the REST endpoint, and the SignalR delta channel SHALL remain unchanged.

#### Scenario: No new network surface

- **WHEN** the user searches, filters, or downloads
- **THEN** no new server endpoint or query parameter SHALL be exercised beyond those already used by Phase 1/2

### Requirement: Phase 1/2 log display and live append do not regress

The line-by-line rendering (timestamp, source, text), the truncation indicator, the running-state SignalR subscription, the live delta append, and the terminal-state cache invalidation SHALL continue to behave as in Phase 1/2.

#### Scenario: Rendering and live append unchanged

- **WHEN** a running task streams new deltas with no filter active
- **THEN** the lines SHALL render in seq order and SHALL live-append exactly as before this change
- **AND** the truncation indicator SHALL still appear when the retained tail is truncated
