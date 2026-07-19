### Requirement: Header omits the inline prev/next sibling navigation

The session page header SHALL NOT render a prev/next sibling navigation slot (`data-testid="session-sibling-navigation-slot"`) as part of its standard wide-viewport layout. Sibling navigation in the header is removed because the `SiblingSessionsSidebar` already exposes the full sibling list (including each sibling's status and the current-session marker) and provides the same navigational affordance. The header MUST NOT render the prev/next navigation on viewport widths where the `SiblingSessionsSidebar` is rendered.

#### Scenario: Header renders no prev/next navigation on wide viewports
- **WHEN** the session page renders at a viewport width where the `SiblingSessionsSidebar` is rendered alongside the transcript
- **THEN** the header SHALL NOT contain a sibling navigation slot
- **AND** no element with `data-testid="session-sibling-navigation-slot"` SHALL be present in the header

#### Scenario: Sidebar remains the single sibling-navigation source on wide viewports
- **WHEN** the session page renders at a wide viewport with siblings available
- **THEN** the `SiblingSessionsSidebar` SHALL render the full list of siblings with their status and a current-session marker
- **AND** the user SHALL be able to navigate to any sibling from the sidebar

### Requirement: Narrow viewport keeps a degraded prev/next sibling navigation

On viewport widths where the `SiblingSessionsSidebar` is not rendered (the layout collapses to a single column with no sidebar), the session page SHALL provide a degraded prev/next sibling navigation affordance inside the header so that navigation remains reachable when the sidebar is unavailable. This degraded affordance SHALL be visually lighter than the wide-viewport sidebar (for example inline links/buttons, no expanded list) and SHALL carry a stable test selector identifying the narrow-viewport fallback.

#### Scenario: Narrow viewport exposes a degraded prev/next in the header
- **WHEN** the session page renders at a viewport width where the `SiblingSessionsSidebar` is not rendered
- **THEN** the header SHALL expose a prev/next sibling navigation affordance that allows navigation to the previous and next sibling
- **AND** that affordance SHALL carry a test selector (for example `data-testid="session-sibling-navigation-slot" data-viewport="narrow"`) that distinguishes it from the removed wide-viewport slot

#### Scenario: Narrow viewport never renders both sidebar and header nav
- **WHEN** the session page renders at any viewport width
- **THEN** the header SHALL render the prev/next sibling navigation slot only when the sidebar is not rendered
- **AND** the sidebar and the header sibling-navigation slot SHALL NOT both be rendered at the same time

### Requirement: Sibling navigation dedup is presentational only

The sibling-navigation deduplication SHALL be purely presentational. It SHALL NOT alter the underlying sibling list (issue, sessions, ordering), the sibling data source, or the API used to load siblings. The same sibling set SHALL produce the same navigable URLs after the change; only the placement of the navigation surface changes.

#### Scenario: Underlying sibling data and URLs are unchanged
- **WHEN** the session page renders with the same sibling set
- **THEN** the URL for the previous sibling, the next sibling, and any sibling in the sidebar SHALL match the previous behavior
- **AND** no sibling data field SHALL be added or removed by this change
