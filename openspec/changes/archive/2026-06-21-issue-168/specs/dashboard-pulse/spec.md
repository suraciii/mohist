## ADDED Requirements

### Requirement: Pulse zone renders pipeline capacity summary

The Dashboard `Pulse` zone SHALL render a capacity header that surfaces live pipeline health at a glance. The header SHALL display slot usage as `active/max` and SHALL display the status counts `active`, `waiting`, `completed`, and `failed` derived from the agent activity summary. The values SHALL reflect the same summary the Activity page consumes.

#### Scenario: Capacity header shows active and max slots

- **WHEN** the Pulse zone renders and the agent activity summary reports `slots.active` and `slots.max`
- **THEN** the Pulse zone SHALL display slot usage as `active/max`
- **AND** the displayed values SHALL match the activity summary's `slots.active` and `slots.max`

#### Scenario: Status counts reflect activity summary

- **WHEN** the Pulse zone renders and the agent activity summary reports `active`, `waiting`, `completed`, and `failed` counts
- **THEN** the Pulse zone SHALL display those four counts in the capacity header
- **AND** the counts SHALL equal the activity summary's values at render time

#### Scenario: Zero capacity renders zeroed summary

- **WHEN** the Pulse zone renders and no slots are in use
- **THEN** the capacity header SHALL render with `0` for active and the configured max
- **AND** the status counts SHALL render as `0`

### Requirement: Pulse zone renders compact active-session cards

The Pulse zone SHALL render one compact card per active session. Each compact card SHALL be a slimmed variant of the Activity page's `ActiveSessionCard`, not a full replication of it. Each card SHALL display the session's issue number, issue stage, current task title, task progress when available, token usage and cost when available, and a context-health color derived from context usage. The Pulse zone SHALL NOT render session replay, transcript, or historical capacity curves.

#### Scenario: Active session card shows identifying fields

- **WHEN** the Pulse zone renders an active session card
- **THEN** the card SHALL display the session's issue number and issue stage
- **AND** the card SHALL display the current task title or task description when available

#### Scenario: Active session card shows task progress

- **WHEN** an active session has `taskProgress` with `completed` and `total`
- **THEN** the compact card SHALL display task progress derived from those fields

#### Scenario: Active session card shows token usage and cost

- **WHEN** an active session reports token usage (`totalTokens`) or cost (`costAmount`)
- **THEN** the compact card SHALL display the available token usage and cost figures
- **AND** the card SHALL omit fields whose underlying values are absent rather than rendering empty placeholders

#### Scenario: Active session card shows context-health color

- **WHEN** an active session reports context usage
- **THEN** the compact card SHALL render a context-health indicator using the `green`, `yellow`, `red` convention shared with the Activity page
- **AND** the color SHALL be derived from the same context-usage thresholds used elsewhere in the Web UI

#### Scenario: Pulse zone excludes replay and history

- **WHEN** the Pulse zone renders active session cards
- **THEN** the cards SHALL NOT include session transcript replay, full activity timeline, or historical capacity curves
- **AND** those concerns SHALL remain the responsibility of the Session and Activity pages

### Requirement: Pulse zone renders empty state when no active sessions

The Pulse zone SHALL render an empty state when there are no active sessions. The empty state SHALL replace the capacity summary's active-session list while still allowing the capacity header to render.

#### Scenario: No active sessions shows empty state

- **WHEN** the Pulse zone renders and the active session list is empty
- **THEN** the Pulse zone SHALL render an empty-state affordance
- **AND** the Pulse zone SHALL NOT render any active-session compact cards

#### Scenario: Empty state does not suppress capacity header

- **WHEN** there are no active sessions
- **THEN** the capacity header SHALL still render with `0` active and the configured max
- **AND** the empty state SHALL render in the session-card area rather than replacing the whole zone

### Requirement: Pulse zone consumes existing activity data sources

The Pulse zone SHALL source all of its data from the existing `useAgentActivity` and `useAgentStatus` hooks that the Activity page and App Shell already use. The Pulse zone SHALL NOT introduce a new query, a new backend endpoint, a new SSE event, or any domain-layer read or write. The Pulse zone SHALL NOT trigger domain actions or perform writes.

#### Scenario: Pulse zone shares the Activity page data source

- **WHEN** the Pulse zone renders
- **THEN** its data SHALL come from the same `useAgentActivity` query the Activity page uses
- **AND** it SHALL NOT issue a separate fetch, query key, or backend call for the same data

#### Scenario: Pulse zone introduces no backend or domain changes

- **WHEN** the Pulse zone is implemented
- **THEN** no new HTTP endpoint, runner query, persistence field, or domain aggregate SHALL be introduced for it
- **AND** the Pulse zone SHALL NOT perform writes to issue, session, workflow, or pipeline state

#### Scenario: Pulse zone does not replace the Activity page

- **WHEN** the Pulse zone renders a compact live summary
- **THEN** the Activity page SHALL remain available and unchanged
- **AND** the Pulse zone SHALL NOT duplicate the Activity page's full session list or waiting/recent sections
