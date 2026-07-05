### Requirement: Web consumes the agreed line element type without double-parsing

The `useLogs` hook SHALL consume the per-line element type returned by `/api/logs/tail` directly. It SHALL NOT run `JSON.parse` on elements the server already parsed, so `level`/`time`/`service`/`message` are preserved instead of being silently lost.

#### Scenario: Fields are preserved from server elements

- **WHEN** `useLogs` receives a response whose `lines` are the agreed structured element type
- **THEN** it SHALL expose `level`/`time`/`service`/`message` from those elements directly
- **AND** SHALL NOT re-parse the elements via `JSON.parse`

#### Scenario: Rows render the preserved fields

- **WHEN** the Logs page renders entries from the consumed elements
- **THEN** each row SHALL display the level, time, service (when present), and message fields populated from the server element
- **AND** SHALL NOT display blank level/time fields caused by a failed client-side parse

### Requirement: Source-aware `File:` line

The Logs page SHALL render a `File:` source line from the source identity in the `/api/logs/tail` response, identifying which source the displayed lines came from, rather than leaving the source line blank/`undefined`.

#### Scenario: File line reflects the real source identity

- **WHEN** the `/api/logs/tail` response carries a real source identity
- **THEN** the page SHALL render the `File:` line using that source identity

### Requirement: Actionable unavailable/empty diagnostic

When runtime logs are unavailable, the Logs page SHALL render an actionable diagnostic that states the expected log location and a human-readable reason, instead of a bare "No logs available". This unavailable diagnostic SHALL be distinct from the "no matching logs" filtered-empty state, so a user debugging a server problem can tell whether logs are genuinely empty or simply not being captured.

#### Scenario: Unavailable diagnostic shows location and reason

- **WHEN** `/api/logs/tail` reports the source-unavailable state
- **THEN** the page SHALL render a diagnostic that includes the expected log location and the reason logs are unavailable
- **AND** SHALL NOT render a bare "No logs available" message

#### Scenario: Available-but-empty does not show the unavailable diagnostic

- **WHEN** the source is available but the current view has zero entries (e.g. no new lines, or all filtered out)
- **THEN** the page SHALL NOT show the unavailable diagnostic

### Requirement: Filtering, search, export, and auto-follow operate against the real source and agreed type

Level filtering, search, export, and auto-follow SHALL operate against the agreed per-line element type from the real source. These features SHALL remain functional after the contract/type alignment; they SHALL NOT be broken by moving to the agreed element type.

#### Scenario: Level filter applies to element levels

- **WHEN** a user toggles a level filter
- **THEN** only entries whose `level` matches the enabled set SHALL be displayed
- **AND** entries with no level SHALL be handled consistently

#### Scenario: Search filters across entry fields

- **WHEN** a user enters a search query
- **THEN** only entries whose message/service/raw text matches the query SHALL be displayed

#### Scenario: Export emits the currently filtered entries

- **WHEN** a user exports
- **THEN** the export SHALL contain the currently filtered entries in the agreed element representation

#### Scenario: Auto-follow continues polling with the incremental cursor

- **WHEN** auto-follow is enabled
- **THEN** the page SHALL continue polling `/api/logs/tail` with the incremental cursor and append new lines
- **AND** SHALL respect the page visibility and pause-on-scroll-up behavior
