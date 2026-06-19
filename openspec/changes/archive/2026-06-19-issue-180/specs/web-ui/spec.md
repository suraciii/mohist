## REMOVED Requirements

### Requirement: Issue Detail page renders Activity event timeline panel

**Reason:** The Activity event timeline is no longer rendered as a panel in the Issue Detail main content column. It is now accessed through an on-demand dialog via the `issue-detail-activity-dialog` capability. Rendering the panel between the diff/commits area and Comments contradicts the new zero-footprint main content area.

**Migration:** See `issue-detail-activity-dialog` — "Issue Detail opens Activity event timeline in an on-demand dialog". The stage/task view and the runtime decision surface remain available on Issue Detail unchanged; only the inline Activity panel location is removed.

## MODIFIED Requirements

### Requirement: Web client fetches merged issue events from the events endpoint

The Web client SHALL call `GET /api/projects/{projectRef}/issues/{number}/events` to load the merged chronological event feed for the current issue. The response SHALL be consumed as the seed data for the Activity timeline. The Web client SHALL NOT issue this request during Issue Detail initial load; it SHALL issue it when the Activity surface is opened (see `issue-detail-activity-dialog`).

#### Scenario: Activity surface requests events when opened

- **WHEN** the Activity surface is opened for an issue
- **THEN** the Web client SHALL request `GET /api/projects/{projectRef}/issues/{number}/events`
- **AND** the returned events SHALL be rendered in the Activity timeline

#### Scenario: Issue Detail initial load does not request events

- **WHEN** Issue Detail is opened for an issue and the Activity surface is not opened
- **THEN** the Web client SHALL NOT request `GET /api/projects/{projectRef}/issues/{number}/events`
- **AND** no event fetch SHALL occur as part of Issue Detail initial load

#### Scenario: Events endpoint returns empty list

- **WHEN** the events endpoint returns an empty array for an issue with no events
- **THEN** the Activity timeline SHALL show its empty state
- **AND** the Web client SHALL NOT treat the empty response as an error

### Requirement: Live event path accumulates events for timeline display

The Web's live event handling path SHALL preserve the existing cache-invalidation and toast behavior for every live issue or workflow event regardless of whether the Activity surface is open. The live path SHALL forward issue and workflow events to the Activity timeline accumulator only while the Activity surface is open. The timeline SHALL deduplicate accumulated live events against loaded history so events are not displayed twice. Events that arrive while the Activity surface is closed SHALL be recovered by re-fetching the persisted history on the next open rather than by live accumulation.

#### Scenario: Live event is appended while the Activity surface is open

- **WHEN** the Activity surface is open for the current issue
- **AND** a SignalR event arrives that is an issue-level or workflow-level lifecycle event in the existing event vocabulary
- **THEN** the event SHALL be forwarded to the Activity timeline accumulator
- **AND** the timeline SHALL append the event if it is not already present

#### Scenario: Existing cache invalidation and toast behavior is preserved

- **WHEN** a live event arrives for the current issue regardless of whether the Activity surface is open
- **THEN** the existing `queryClient.invalidateQueries` calls and toast notifications SHALL continue to fire as before
- **AND** the timeline accumulation gating SHALL NOT suppress or replace the existing behavior

#### Scenario: Duplicate event is not displayed twice

- **WHEN** a live event arrives that is already present in the timeline from the loaded history
- **THEN** the timeline SHALL NOT render a duplicate row for that event

#### Scenario: Events arriving while the surface is closed are recovered on reopen

- **WHEN** the Activity surface is closed and live events arrive for the issue
- **THEN** those events SHALL be recovered by re-fetching the persisted history the next time the surface is opened
- **AND** they SHALL NOT require live accumulation while the surface is closed

## ADDED Requirements

### Requirement: Issue Detail follows a unified spacing rhythm with group-tight and group-gap whitespace

Issue Detail SHALL use a single unified spacing scale (avoiding ad-hoc scattered values). Related elements SHALL be grouped tightly within a group, and distinct groups SHALL be separated by larger whitespace so module boundaries are legible. Whitespace grouping SHALL be used in place of decorative borders to separate modules wherever practical. Within list surfaces (Tasks, Checks, and event rows), items SHALL be grouped tightly with group-level gaps between groups rather than uniformly stacked. The Issue Detail first-screen next-action area (the runtime decision surface and its actions) SHALL have adequate breathing room and SHALL NOT be cramped against neighboring modules.

#### Scenario: A unified spacing scale is used

- **WHEN** Issue Detail source and rendering are inspected
- **THEN** spacing SHALL follow a single unified scale
- **AND** ad-hoc scattered spacing values SHALL be avoided

#### Scenario: Modules are separated by whitespace rather than decorative borders

- **WHEN** Issue Detail renders distinct sections and right-rail cards
- **THEN** section boundaries and card boundaries SHALL be conveyed primarily through group-level whitespace gaps
- **AND** decorative borders SHALL be reduced where whitespace grouping can separate modules

#### Scenario: List items group tightly with group gaps

- **WHEN** Issue Detail renders Tasks, Checks, or event rows
- **THEN** items within a group SHALL be tightly spaced
- **AND** group-level gaps SHALL separate distinct groups

#### Scenario: List rows are not over-stacked

- **WHEN** Issue Detail renders Tasks, Checks, or event rows
- **THEN** individual rows SHALL NOT cram or over-stack their inline elements
- **AND** row-to-row spacing SHALL relieve the prior over-dense packing while preserving the group rhythm

#### Scenario: First-screen next-action area has breathing room

- **WHEN** Issue Detail renders the first-screen runtime decision surface and its actions
- **THEN** the next-action area SHALL have adequate surrounding whitespace
- **AND** it SHALL NOT be cramped directly against neighboring modules
