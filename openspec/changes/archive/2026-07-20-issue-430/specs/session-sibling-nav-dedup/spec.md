### Requirement: Header omits the inline prev/next sibling navigation

The session page header SHALL NOT render a prev/next sibling navigation slot (`data-testid="session-sibling-navigation-slot"`) as part of its standard wide-viewport layout. Sibling navigation in the header is removed because the `SiblingSessionsSidebar` already exposes the full sibling list (including each sibling's status and the current-session marker) and provides the same navigational affordance. The header MUST NOT render the prev/next navigation on viewport widths at or above the wide-viewport breakpoint where the `SiblingSessionsSidebar` is visible alongside the transcript. The sidebar's own visibility mechanism (CSS via the parent's `xl:flex-row` on `SessionDetailShell`) is preserved unchanged — no new render gate is added.

#### Scenario: Header renders no prev/next navigation on wide viewports
- **WHEN** the session page renders at a viewport width where the `SiblingSessionsSidebar` is visible alongside the transcript (above the `xl` breakpoint)
- **THEN** the header SHALL NOT contain a sibling navigation slot
- **AND** no element with `data-testid="session-sibling-navigation-slot"` SHALL be present in the header

#### Scenario: Sidebar remains the single sibling-navigation source on wide viewports
- **WHEN** the session page renders at a wide viewport with siblings available
- **THEN** the `SiblingSessionsSidebar` SHALL render the full list of siblings with their status and a current-session marker
- **AND** the user SHALL be able to navigate to any sibling from the sidebar

### Requirement: Narrow viewport keeps a degraded prev/next sibling navigation

On viewport widths where the `SiblingSessionsSidebar` is not visible (the layout collapses to a single column with no sidebar — at or below the `xl` breakpoint), the session page SHALL provide a degraded prev/next sibling navigation affordance inside the header so that navigation remains reachable when the sidebar is not visible. This degraded affordance SHALL be visually lighter than the wide-viewport sidebar (for example inline links/buttons, no expanded list) and SHALL carry a stable test selector identifying the narrow-viewport fallback.

#### Scenario: Narrow viewport exposes a degraded prev/next in the header
- **WHEN** the session page renders at a viewport width where the `SiblingSessionsSidebar` is not visible alongside the transcript (at or below the `xl` breakpoint)
- **THEN** the header SHALL expose a prev/next sibling navigation affordance that allows navigation to the previous and next sibling
- **AND** that affordance SHALL carry a test selector (for example `data-testid="session-sibling-navigation-slot" data-viewport="narrow"`) that distinguishes it from the removed wide-viewport slot

#### Scenario: Wide and narrow viewports never both expose visible sibling navigation
- **WHEN** the session page renders at any viewport width
- **THEN** the visible sibling-navigation surface SHALL appear in exactly one place: the sidebar at wide viewports, the header prev/next slot at narrow viewports
- **AND** the user-visible prev/next affordance SHALL NOT appear in both locations simultaneously

### Requirement: Sibling navigation dedup is presentational only

The sibling-navigation deduplication SHALL be purely presentational. It SHALL NOT alter the underlying sibling list (issue, sessions, ordering), the sibling data source, the API used to load siblings, or the sidebar's visibility mechanism. The same sibling set SHALL produce the same navigable URLs after the change; only the header's prev/next slot's surface placement changes.

#### Scenario: Underlying sibling data and URLs are unchanged
- **WHEN** the session page renders with the same sibling set
- **THEN** the URL for the previous sibling, the next sibling, and any sibling in the sidebar SHALL match the previous behavior
- **AND** no sibling data field SHALL be added or removed by this change
