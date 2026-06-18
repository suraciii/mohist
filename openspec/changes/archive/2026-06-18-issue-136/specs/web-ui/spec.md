## ADDED Requirements

### Requirement: Issue Detail page renders Activity event timeline panel

The Issue Detail page SHALL render an Activity event timeline panel in the main column between the diff/commits area and the Comments section. The Activity panel SHALL be visible as supporting context alongside the existing stage/task view and the runtime decision surface without displacing either.

#### Scenario: Activity panel appears between diff/commits and comments

- **WHEN** a user opens the issue detail page for an issue
- **THEN** the page SHALL render the Activity timeline panel below the diff/commits area
- **AND** the Activity timeline panel SHALL appear above the Comments section

#### Scenario: Stage/task view remains visible

- **WHEN** the Activity timeline panel is rendered on the issue detail page
- **THEN** the existing stage/task projection view (`WorkflowView`) SHALL remain visible above the Activity panel
- **AND** the Activity panel SHALL NOT replace or hide the stage/task view

#### Scenario: Runtime decision surface remains available

- **WHEN** the Activity timeline panel is rendered on the issue detail page
- **THEN** the runtime decision surface (recovery actions, approval controls, convergence panel) SHALL remain available in its existing location
- **AND** the Activity panel SHALL NOT displace the runtime decision surface

### Requirement: Web client fetches merged issue events from the events endpoint

The Web client SHALL call `GET /api/projects/{projectRef}/issues/{number}/events` to load the merged chronological event feed for the current issue. The response SHALL be consumed as the seed data for the event timeline.

#### Scenario: Issue detail page requests events on load

- **WHEN** the issue detail page is opened for an issue
- **THEN** the Web client SHALL request `GET /api/projects/{projectRef}/issues/{number}/events`
- **AND** the returned events SHALL be rendered in the Activity timeline

#### Scenario: Events endpoint returns empty list

- **WHEN** the events endpoint returns an empty array for an issue with no events
- **THEN** the Activity timeline SHALL show its empty state
- **AND** the Web client SHALL NOT treat the empty response as an error

### Requirement: Live event path accumulates events for timeline display

The Web's live event handling path SHALL accumulate issue and workflow events received over SignalR for the current issue and forward them to the event timeline for display, in addition to preserving the existing cache-invalidation and toast behavior. The timeline SHALL deduplicate accumulated live events against loaded history so events are not displayed twice.

#### Scenario: Live event is appended to timeline

- **WHEN** a SignalR event arrives for the issue currently viewed on the issue detail page
- **AND** the event is an issue-level or workflow-level lifecycle event in the existing event vocabulary
- **THEN** the event SHALL be forwarded to the Activity timeline accumulator
- **AND** the timeline SHALL append the event if it is not already present

#### Scenario: Existing cache invalidation and toast behavior is preserved

- **WHEN** the live event path forwards an event to the timeline accumulator
- **THEN** the existing `queryClient.invalidateQueries` calls and toast notifications SHALL continue to fire as before
- **AND** the timeline accumulation SHALL NOT suppress or replace the existing behavior

#### Scenario: Duplicate event is not displayed twice

- **WHEN** a live event arrives that is already present in the timeline from the loaded history
- **THEN** the timeline SHALL NOT render a duplicate row for that event
