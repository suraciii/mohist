### Requirement: Session header metadata renders as a single row

The session page header SHALL present all per-session metadata (session name, status badge, stage chip, model, turn count, last activity time, total duration, and session id) in a single visual row that sits beneath the context breadcrumb (back link / issue title / workflow context). The header SHALL NOT split this metadata across multiple rows, and SHALL NOT introduce intermediate divider glyphs (such as `·` separators or per-item responsive hidden/visible breakpoints) that visually break the single-row invariant at the supported viewport sizes. The metadata row SHALL stay horizontally flexible (truncate / wrap to a second row only when the viewport cannot accommodate it), and SHALL remain a sibling of the context breadcrumb within the header — the context breadcrumb is preserved unchanged.

#### Scenario: Header metadata sits on one row at desktop width
- **WHEN** the session page renders at a viewport wide enough to fit all available metadata
- **THEN** the session name, status badge, stage chip, model, turn count, last activity time, total duration, and session id SHALL all render on the same visual row beneath the context breadcrumb
- **AND** the header SHALL NOT introduce intermediate divider glyphs (for example `·`) between those items

#### Scenario: Header metadata collapses gracefully when narrow
- **WHEN** the available viewport width cannot fit the full metadata on one row
- **THEN** the metadata row SHALL wrap to a second row of the same metadata block (not push unrelated content above or below)
- **AND** no metadata item SHALL be silently dropped or hidden without an accessible alternative

### Requirement: Session id is rendered as a one-click copy affordance

The session id SHALL be rendered as a one-click copy control inside the header metadata row. Activating the control SHALL copy the complete session id value to the system clipboard and SHALL expose a transient confirmation that the copy succeeded. The control SHALL NOT truncate the session id value (for example, it SHALL NOT display only the first 8 characters as plain text without a way to copy the full value).

#### Scenario: Session id copies the full value on activation
- **WHEN** a user activates the session id copy control in the header
- **THEN** the complete `meta.sessionId` value SHALL be written to the clipboard
- **AND** a transient confirmation indicating the copy succeeded SHALL be shown

#### Scenario: Session id copy control exposes the full id for accessibility
- **WHEN** the session id copy control renders
- **THEN** the full session id SHALL be exposed via an accessible name or label (for example `aria-label="Copy session id <full id>"` or a tooltip containing the full id)
- **AND** the rendered label MUST NOT be limited to a truncated prefix as the sole readable value

### Requirement: Header metadata exposes stable test selectors

The session header metadata row SHALL expose stable `data-testid` and `data-*` attributes for each metadata item (session name, status badge, stage chip, model, turn count, last activity time, total duration, session id, and any action control rendered inside the row). These selectors SHALL remain stable across the single-row rewrite so that downstream tests and embedders can locate each item without depending on layout.

#### Scenario: Each metadata item carries a stable test selector
- **WHEN** the session header renders with all metadata items present
- **THEN** each item SHALL render with a stable `data-testid` (for example `data-testid="session-header-status"`, `data-testid="session-header-stage"`, `data-testid="session-header-turn-count"`, `data-testid="session-header-last-activity"`, `data-testid="session-header-duration"`, `data-testid="session-header-session-id"`) and SHALL also expose its value via a `data-*` attribute where applicable (for example `data-stage`, `data-turn-count`, `data-session-id`)

### Requirement: Header metadata is presentational only

The header metadata rewrite SHALL be purely presentational. It SHALL NOT alter `SessionDataSourceResult`, `SessionMetadata`, the event protocol, the liveness gate, or the values shown for any metadata item. The same metadata SHALL produce the same observable values after the rewrite; only the layout and the session-id affordance change.

#### Scenario: Underlying data fields are unchanged
- **WHEN** the session header renders with the same metadata input
- **THEN** the values shown for status, stage, model, turn count, last activity time, total duration, and session id SHALL match the input fields unchanged
- **AND** no new metadata field SHALL be introduced on `SessionMetadata`
