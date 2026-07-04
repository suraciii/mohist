# issue-detail-mobile-action-bar Specification

## Requirements

### Requirement: Single Primary Action Surfaced in a Bottom Floating Bar

On narrow viewports the issue detail page SHALL surface exactly one primary runtime action — the action carried by `decision.primary` (start, stop, approve, send-back, retry, resume, or rerun) — inside a floating action bar anchored to the bottom of the scroll viewport. The bar SHALL reuse the unchanged `deriveRuntimeDecision` output and the existing issue-detail mutations verbatim and SHALL NOT introduce a new data source or a second decision surface.

#### Scenario: Running issue surfaces Stop as the primary action

- **WHEN** a narrow viewport renders an issue whose `decision.primary` resolves to stop
- **THEN** the bottom floating action bar renders a single primary Stop control bound to the existing stop mutation
- **AND** no competing primary action control for that issue renders elsewhere on the page

#### Scenario: Approval-required issue surfaces Approve as the primary action

- **WHEN** a narrow viewport renders an issue whose `decision.primary` resolves to approve
- **THEN** the bottom floating action bar renders the Approve control as its primary action

#### Scenario: Backlog issue ready to start surfaces Start as the primary action

- **WHEN** a narrow viewport renders a ready backlog issue whose `decision.primary` resolves to start
- **THEN** the bottom floating action bar renders the Start control as its primary action

### Requirement: Bar Renders Only When a Primary Action Exists

The floating action bar SHALL render on narrow viewports only when `decision.primary` is non-null. When no primary action exists — including the done state, archived state, and any other no-primary runtime summary — the bar SHALL NOT render and SHALL reserve no screen space.

#### Scenario: Done state shows no bar

- **WHEN** a narrow viewport renders an issue resolved to the done runtime summary where `decision.primary` is null
- **THEN** the bottom floating action bar does not render

#### Scenario: Running state shows the bar

- **WHEN** a narrow viewport renders an issue whose `decision.primary` is non-null
- **THEN** the bottom floating action bar renders

### Requirement: Thumb-Zone Placement Above the Global Bottom Nav Without Overlap

The floating action bar SHALL be fixed to the bottom of the narrow viewport within the thumb-reachable zone and SHALL sit above the global `MobileBottomNav` when that nav is present, so the two never overlap. Because the narrow breakpoint (`max-width: 1023.98px`, below `lg`/1024px) is wider than the global nav visibility breakpoint (`md`/768px), the bar SHALL apply the nav-offset only while the global bottom nav is actually visible; on narrow widths where the global nav is hidden the bar SHALL anchor to the bottom edge without reserving the nav's footprint.

#### Scenario: Bar offsets above the visible global bottom nav on a phone width

- **WHEN** the viewport width is below the global bottom nav breakpoint (for example 375px) and a primary action exists
- **THEN** the floating action bar is positioned directly above the global bottom nav
- **AND** the bar's bottom edge does not overlap the global bottom nav's occupied region

#### Scenario: Bar anchors to the bottom edge when the global nav is hidden

- **WHEN** the viewport width is narrow but at or above the global bottom nav breakpoint (for example 900px) and a primary action exists
- **THEN** the floating action bar anchors to the bottom edge of the viewport
- **AND** no space is reserved for the absent global bottom nav

### Requirement: Narrow-Viewport-Only Rendering

The floating action bar SHALL render only on narrow viewports (below `lg`/1024px). On tablet (`lg`/1024px) and wider viewports the bar SHALL NOT render and the primary action SHALL remain anchored in the status-header tier instead.

#### Scenario: Tablet width does not render the bar

- **WHEN** the viewport width is at or above `lg` (1024px)
- **THEN** the bottom floating action bar does not render
- **AND** the primary action renders within the status-header tier

### Requirement: Bottom Padding Reservation Prevents Content Obscuring

The detail page scroll body SHALL reserve bottom padding on narrow viewports so the last item in the reading flow (including the last comment) is never obscured by the floating action bar nor by the global bottom nav. The reservation SHALL account for whichever of those elements is present at the current width.

#### Scenario: Last comment remains visible above the bar and the global nav

- **WHEN** a narrow viewport at a phone width renders a primary action bar and a global bottom nav and the reading flow contains a final comment
- **THEN** scrollable bottom padding is reserved so the final comment can be scrolled fully into view without being covered by either the floating action bar or the global bottom nav

#### Scenario: No padding reserved for the bar when no primary action exists

- **WHEN** a narrow viewport renders an issue with no primary action (for example done)
- **THEN** no padding is reserved on behalf of the absent floating action bar
- **AND** any bottom padding reserved for the global bottom nav alone still applies
