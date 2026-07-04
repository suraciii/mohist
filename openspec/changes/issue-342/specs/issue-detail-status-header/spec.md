# issue-detail-status-header Specification

## Requirements

### Requirement: Read-Only Sticky Status Headline

The issue detail page SHALL render a status headline pinned to the top of the scroll viewport that presents the current runtime situation, the current workflow stage and progress, the current task title, and the issue identity. The headline region SHALL be strictly read-only: it SHALL NOT contain any runtime action buttons (start, stop, approve, send-back, retry, resume, rerun). The headline SHALL stay pinned while the page body scrolls and SHALL remain visible while a destructive-action confirmation drawer is open on narrow viewports.

#### Scenario: Headline stays pinned while scrolling

- **WHEN** the user scrolls the detail page body vertically on any viewport
- **THEN** the status headline remains pinned at the top edge of the scroll viewport

#### Scenario: Headline contains no action buttons on a narrow viewport

- **WHEN** the detail page renders on a narrow viewport
- **THEN** the status headline region renders the runtime situation, stage, current task, and identity
- **AND** no start, stop, approve, send-back, retry, resume, or rerun control renders inside the headline region

#### Scenario: Headline stays visible while the confirmation drawer is open

- **WHEN** a destructive-action confirmation drawer is open on a narrow viewport
- **THEN** the pinned status headline remains visible and uncovered

### Requirement: Action Surface Placement Splits by Viewport

The single runtime decision/action surface — the home for approve, send-back, stop, start, retry, resume, and rerun — SHALL be anchored within the status-header tier on desktop and tablet viewports (`lg`/1024px and wider). On narrow viewports (below `lg`/1024px) the action surface SHALL be stripped from the status-header tier entirely and the single primary action SHALL relocate to the bottom floating action bar; the status-header tier SHALL NOT render the action surface on narrow viewports.

#### Scenario: Desktop anchors the action surface in the header tier

- **WHEN** the detail page renders on a viewport at or above `lg` (1024px)
- **THEN** the runtime decision/action surface renders within the status-header tier
- **AND** the primary action control is reachable from the status-header tier

#### Scenario: Narrow viewport strips the action surface from the header tier

- **WHEN** the detail page renders on a narrow viewport (below `lg`/1024px)
- **THEN** the status-header tier does not render the runtime decision/action surface
- **AND** the single primary action is rendered in the bottom floating action bar instead

### Requirement: Single Glanceable Status Region

The runtime situation, the current workflow stage, the stage progress, and the current task title SHALL be presented together inside the single status-headline region rather than scattered across separate scrolling cards of equal weight.

#### Scenario: All status facets appear in one headline region

- **WHEN** the issue has an adjudicated runtime situation plus a current stage with progress and a running task title
- **THEN** the headline region displays the runtime situation, the stage, the completed-of-total progress, and the current task title together in one region
- **AND** none of these facets are isolated into independent same-weight cards elsewhere on the page
