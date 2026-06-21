# OpenSpec Capability: runner-detail

### Requirement: Each active work carries full work-level context

For every runner, each tracked active work SHALL expose its work identifier (`workId`), owner kind and owner identity (workflow run id or agent job id), work type (`workType`), stage (`stage`), title (`title`), and associated issue reference (`issue`). The issue reference SHALL include the project id, issue id, and issue number when the dispatch carried one, and SHALL be absent otherwise. This context SHALL be derived entirely from the payload already present on the work dispatch at assign time; the runner SHALL NOT be required to send any additional report to populate it.

#### Scenario: Active work exposes dispatch-time context

- **WHEN** a runner has been assigned a work whose dispatch carries `workId`, `workType`, `stage`, `title`, and an `issue` reference
- **THEN** the runner's active-work view for that work SHALL include all of `workId`, `workType`, `stage`, `title`, and the `issue` reference
- **AND** the field values SHALL match the dispatch payload exactly

#### Scenario: Work without an issue reference still appears

- **WHEN** a runner has been assigned a work whose dispatch carries no issue reference
- **THEN** the active-work view for that work SHALL still include `workId`, `workType`, `stage`, and `title`
- **AND** the `issue` field SHALL be absent (not a placeholder object)

#### Scenario: No extra runner-side reporting is required

- **WHEN** the runner has not sent any message beyond the existing register / heartbeat / poll / report protocol
- **THEN** the active-work context SHALL still be fully populated on the server side
- **AND** it SHALL stay consistent with the last assign or report for that work

### Requirement: All active works are surfaced per runner, bounded by slots

A runner's active-work view SHALL expose every active work the runner is currently tracking, presented as independent items. The number of items SHALL NOT be artificially capped below the runner's active work count, and SHALL NOT exceed the runner's normalized maximum workflow slot count. A single active work SHALL NOT be hidden merely because another is already present.

#### Scenario: Multiple concurrent works each appear

- **WHEN** a runner with a maximum of 3 workflow slots is running 2 works concurrently
- **THEN** the runner's active-work view SHALL contain exactly 2 independent items
- **AND** each item SHALL carry its own `workId`, `stage`, `title`, and issue reference

#### Scenario: Idle runner exposes an empty active-work list

- **WHEN** a runner is online and has no tracked works
- **THEN** the runner's active-work view SHALL be an empty list
- **AND** it SHALL NOT be null

#### Scenario: First work is not privileged over others

- **WHEN** a runner is running more than one work
- **THEN** every tracked work SHALL appear in the active-work view
- **AND** no tracked work SHALL be omitted in favor of another

### Requirement: Single runner can be queried by runner identifier

The server SHALL provide a query that returns a single runner's full detail by its runner identifier. The result SHALL combine the runner's identity (id, kind, hostname, scope, registered-at, build git hash), capabilities (capabilities, coder models, maximum workflow slot count), the full active-work view, and health metrics (status, connection state, last heartbeat). The query SHALL return the runner's detail when the runner is registered to the resolved project scope, and SHALL clearly distinguish an unknown runner from an idle one.

#### Scenario: Known runner returns full detail

- **WHEN** the server is queried for a runner id that is registered to the resolved project
- **THEN** the response SHALL include the runner's identity, capabilities, active-work view, and health metrics

#### Scenario: Idle runner returns detail with empty active work

- **WHEN** the server is queried for an online runner that has no tracked works
- **THEN** the response SHALL still include the runner's identity, capabilities, and health metrics
- **AND** the active-work view SHALL be an empty list

#### Scenario: Unknown runner is distinguishable from idle

- **WHEN** the server is queried for a runner id that is not registered to the resolved project
- **THEN** the response SHALL indicate that the runner was not found
- **AND** it SHALL NOT be confused with a known idle runner

### Requirement: Runner list endpoint exposes multi-item active work per runner

The existing `GET /api/projects/{projectRef}/runners` endpoint SHALL return, for every listed runner, the full multi-item active-work view described above alongside the runner's identity, capabilities, and health metrics. The list response SHALL NOT collapse active works to a single item per runner.

#### Scenario: List endpoint returns all active works per runner

- **WHEN** a client requests `GET /api/projects/{projectRef}/runners` and a listed runner is running 2 works
- **THEN** that runner's entry in the response SHALL contain 2 active-work items
- **AND** each item SHALL carry `workId`, `workType`, `stage`, `title`, and the issue reference when present

#### Scenario: List endpoint preserves identity, capabilities, and health

- **WHEN** a client requests `GET /api/projects/{projectRef}/runners`
- **THEN** each runner entry SHALL continue to include identity, capabilities, and health metrics
- **AND** the active-work view SHALL be additive to the existing fields

### Requirement: HTTP API exposes a single-runner detail endpoint

The server SHALL expose `GET /api/projects/{projectRef}/runners/{runnerId}` to query one runner's full detail. A request for a runner registered to the resolved project SHALL return the single-runner detail view with HTTP 200. A request for a runner id that is not registered to the resolved project SHALL return HTTP 404 with a clear not-found reason. The endpoint SHALL be read-only.

#### Scenario: Detail endpoint returns the runner

- **WHEN** a client requests `GET /api/projects/{projectRef}/runners/{runnerId}` for a registered runner
- **THEN** the server SHALL return HTTP 200 with the single-runner detail view

#### Scenario: Detail endpoint returns 404 for unknown runner

- **WHEN** a client requests `GET /api/projects/{projectRef}/runners/{runnerId}` for an id not registered to the resolved project
- **THEN** the server SHALL return HTTP 404
- **AND** the response body SHALL identify the runner id as not found

#### Scenario: Detail endpoint performs no control action

- **WHEN** a client requests the detail endpoint
- **THEN** the server SHALL NOT mutate runner state, registered info, or tracked works
- **AND** the request SHALL NOT trigger dispatch, heartbeat, or unregister side effects

### Requirement: Web UI provides a runner detail page

The Web UI SHALL provide a runner detail page that renders the single runner's full identity (id, kind, hostname, scope, registered-at, build git hash), capabilities (capabilities, coder models, maximum workflow slot count), every active work with its work identifier, work type, stage, and title, and health metrics (status, connection state, last heartbeat). When an active work carries an issue reference, the page SHALL render a navigable link to the associated issue. When the runner has multiple active works, each SHALL be presented as an independent row.

#### Scenario: Detail page renders full runner detail

- **WHEN** a user opens the runner detail page for a busy runner
- **THEN** the page SHALL render the runner's identity, capabilities, every active work's context, and health metrics

#### Scenario: Active work links to its associated issue

- **WHEN** the runner detail page renders an active work whose issue reference includes project id and issue number
- **THEN** the active work row SHALL render a link that navigates to that issue's page
- **AND** the link SHALL target the issue identified by the issue reference

#### Scenario: Work without an issue reference renders without a link

- **WHEN** the runner detail page renders an active work whose dispatch carried no issue reference
- **THEN** the row SHALL still display the work's stage and title
- **AND** it SHALL NOT render a broken or placeholder issue link

#### Scenario: Multiple active works render independently

- **WHEN** the runner detail page renders for a runner running 3 works concurrently
- **THEN** the page SHALL render 3 independent active-work rows
- **AND** each row SHALL carry its own stage, title, and issue link where applicable

#### Scenario: Unknown runner is surfaced clearly

- **WHEN** a user opens the runner detail page for a runner id that returns 404 from the detail endpoint
- **THEN** the page SHALL surface a clear not-found state
- **AND** it SHALL NOT render a partially populated runner

### Requirement: Runner list links into the runner detail page

Each runner presented in the Web UI runner list SHALL be navigable to its detail page. Activating a runner entry SHALL navigate to that runner's detail page using its runner identifier. The list SHALL continue to render the existing summary information; navigation SHALL be additive and SHALL NOT remove existing list behavior.

#### Scenario: Clicking a runner opens its detail page

- **WHEN** a user activates a runner entry in the runner list
- **THEN** the UI SHALL navigate to the runner detail page keyed by that runner's identifier

#### Scenario: List summary behavior is preserved

- **WHEN** the runner list renders
- **THEN** each entry SHALL continue to show the runner's identity, status, scope, and health indicators
- **AND** the new navigation SHALL NOT displace the existing summary content

### Requirement: CLI exposes a runner show subcommand

The `mo` CLI SHALL provide a `runner show <runnerId>` subcommand that prints a single runner's full detail. The output SHALL include the runner's identity, capabilities, every active work's context (work identifier, work type, stage, title, issue number when present), and health metrics. The subcommand SHALL resolve the target project via the standard project selection rules and SHALL consume the single-runner detail endpoint.

#### Scenario: Show prints a runner's full detail

- **WHEN** a user runs `mo runner show <runnerId>` for a registered runner
- **THEN** the command SHALL print the runner's identity, capabilities, all active works' context, and health metrics
- **AND** the active-work section SHALL list each work independently

#### Scenario: Show reports unknown runner clearly

- **WHEN** a user runs `mo runner show <runnerId>` for an id not registered to the resolved project
- **THEN** the command SHALL exit with a non-zero status
- **AND** the output SHALL clearly state that the runner was not found

#### Scenario: Show on idle runner prints detail with no active works

- **WHEN** a user runs `mo runner show <runnerId>` for an online runner with no tracked works
- **THEN** the command SHALL print the runner's identity, capabilities, and health metrics
- **AND** the active-work section SHALL indicate that there are no active works

### Requirement: Runner detail capability is strictly read-only

The runner detail capability SHALL be observability-only. It SHALL NOT introduce any control action over runners, SHALL NOT persist historical execution records or statistics, SHALL NOT alter the register / heartbeat / dispatch protocol, and SHALL NOT stream real-time logs. Any state surfaced SHALL be a projection of the runner's current runtime state.

#### Scenario: No control actions are introduced

- **WHEN** the runner detail endpoints, Web page, or CLI subcommand are exercised
- **THEN** none of them SHALL expose start, stop, pause, drain, evict, or assign actions against a runner

#### Scenario: No historical records are persisted

- **WHEN** a runner completes or loses a work
- **THEN** the capability SHALL NOT persist a historical execution record or statistic for that work
- **AND** the active-work view SHALL reflect only currently tracked works

#### Scenario: Dispatch protocol is unchanged

- **WHEN** the active-work context is surfaced
- **THEN** the register / heartbeat / poll / report protocol SHALL remain unchanged
- **AND** no new mandatory field SHALL be added to the runner's wire contract
