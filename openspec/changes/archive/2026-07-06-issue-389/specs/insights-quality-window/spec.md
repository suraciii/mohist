### Requirement: AI Quality Card Renders a Single Range-Driven Window

The AI Quality card on the Insights page MUST render exactly one quality window. The card MUST NOT render a second, fixed-window quality panel alongside the primary one. The single window's data MUST correspond to the page's selected time range (7d/30d/90d).

#### Scenario: Single window follows the selected range

- **WHEN** the user selects 7d, 30d, or 90d on the Insights page
- **THEN** the AI Quality card renders exactly one quality window
- **AND** that window's date span corresponds to the selected range

#### Scenario: No hardcoded fixed-window label

- **WHEN** the AI Quality card renders its window
- **THEN** the window title MUST NOT be a hardcoded "Last 7 days" label
- **AND** the card MUST NOT render a second fixed-window panel

### Requirement: AI Quality Card Window Title Shows the Actual Date Span

The AI Quality card's window title MUST display the actual date range the window covers, so a user can read the window's caliber from the title without inferring it from the range selector.

#### Scenario: Window title reflects the actual bounds

- **WHEN** the AI Quality card renders a non-empty window covering a `[from, to]` span
- **THEN** the window title MUST present both the `from` and `to` dates of that window

### Requirement: AI Quality Card Empty State on Zero Samples

The AI Quality card MUST enter its empty state when the window contains zero shipped issues, rather than rendering precise rates without sample-size context.

#### Scenario: Zero-sample window renders the empty state

- **WHEN** the quality window for the selected range has a sample count of zero
- **THEN** the AI Quality card MUST render its empty state
- **AND** MUST NOT render a first-time-right rate or a rework rate as a precise value

### Requirement: Quality Metrics Endpoint Returns a Single Range-Driven Window

The `GET /issues/metrics/quality` endpoint MUST return a single primary quality window whose span is driven by the `range` query parameter (7d/30d/90d, default 30d). The response MUST NOT contain a separate fixed 7-day window. The response's field naming for the primary window MUST reflect the window's actual range-driven semantics — a field MUST NOT carry a fixed-day-count name (for example `window30d`) while holding data sized to a different range.

#### Scenario: range parameter drives the single window span

- **WHEN** the endpoint is called with `range=90d`
- **THEN** the response contains exactly one primary window
- **AND** that window's span is 90 days
- **AND** the response does not contain a fixed 7-day window field

#### Scenario: omitted range defaults to 30 days for the single window

- **WHEN** the endpoint is called without a `range` parameter
- **THEN** the single primary window's span is 30 days
- **AND** the response does not contain a fixed 7-day window field

#### Scenario: field naming matches the actual caliber

- **WHEN** the endpoint is called with any range
- **THEN** the primary window's field name MUST NOT imply a fixed day count that contradicts the range-driven span

### Requirement: Quality Trend and Previous-Window Comparison Scale With Range

The quality trend series and the previous-window first-time-right comparison MUST scale with the selected range. The trend span MUST equal the primary window span, and the previous window MUST be the immediately-preceding window of the same length as the primary window.

#### Scenario: Trend span tracks the selected range

- **WHEN** the endpoint is called with `range=90d`
- **THEN** the trend series spans the same 90-day window as the primary window
- **AND** the trend bucket count corresponds to the selected range

#### Scenario: Previous window precedes the primary window with the same length

- **WHEN** the endpoint is called with `range=90d`
- **THEN** the previous-window comparison covers the 90 days immediately preceding the primary window

### Requirement: Quality Aggregation Algorithm Unchanged

The change to a single-window response structure MUST NOT alter the first-time-right classification, the per-stage rework-rate computation, the ship-time windowing, or the empty-result discriminators. The aggregation formula is preserved; only the response shape and the removal of the fixed 7-day lens change.

#### Scenario: Empty window yields null rates with zero sample count

- **WHEN** the primary window contains no shipped issues
- **THEN** the window's sample count is 0
- **AND** the first-time-right rate and the stage rework rates are null

#### Scenario: First-time-right classification is preserved

- **WHEN** a shipped issue in the window is classified
- **THEN** the first-time-right and rework classification MUST follow the same rules as before this change
