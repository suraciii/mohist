## ADDED Requirements

### Requirement: InboxSubscription is a project-scoped preference model over the four notification kinds

The server SHALL maintain an `InboxSubscription` preference model scoped by exactly one project. The model SHALL expose one enabled/disabled toggle for each of the four MVP notification kinds: `workflow_failed`, `approval_requested`, `issue_started`, and `issue_completed`. The model SHALL key toggles by `NotificationKind`, not by raw CloudEvent type strings. Subscription preferences SHALL affect only the creation of future inbox items; disabling a kind SHALL NOT delete, rewrite, or mark read any existing inbox item, and re-enabling a kind SHALL NOT recreate items for events that were skipped while the kind was disabled.

#### Scenario: Subscription is scoped to exactly one project

- **WHEN** an `InboxSubscription` is stored
- **THEN** it SHALL be keyed to exactly one project
- **AND** it SHALL NOT be addressable without a project scope

#### Scenario: Subscription exposes one toggle per MVP notification kind

- **WHEN** an `InboxSubscription` is read or updated
- **THEN** it SHALL expose exactly one enabled/disabled toggle for each of `workflow_failed`, `approval_requested`, `issue_started`, and `issue_completed`
- **AND** the toggles SHALL be keyed by `NotificationKind`
- **AND** the toggles SHALL NOT be keyed by raw CloudEvent type strings

#### Scenario: Disabling a kind stops future items but leaves existing items untouched

- **WHEN** a notification kind is disabled for a project that already has inbox items of that kind
- **THEN** no new inbox items of that kind SHALL be created for that project thereafter
- **AND** the existing inbox items of that kind SHALL NOT be deleted, rewritten, or marked read

#### Scenario: Re-enabling a kind does not backfill skipped events

- **WHEN** a notification kind is re-enabled for a project after being disabled
- **THEN** future inbox items of that kind SHALL be created again for that project
- **AND** no inbox item SHALL be created for an event that was observed while the kind was disabled

### Requirement: Default subscription is all four kinds enabled

A project with no stored `InboxSubscription` SHALL behave as if all four notification kinds are enabled. A newly created project SHALL behave as if all four kinds are enabled. This default SHALL preserve the project inbox MVP behavior, where every one of the four kinds produces inbox items.

#### Scenario: Project with no stored preferences behaves as all-enabled

- **WHEN** a project has no stored `InboxSubscription`
- **THEN** the project SHALL behave as if `workflow_failed`, `approval_requested`, `issue_started`, and `issue_completed` are all enabled

#### Scenario: Newly created project behaves as all-enabled

- **WHEN** a new project is created
- **THEN** the project SHALL behave as if all four notification kinds are enabled
- **AND** the MVP inbox behavior SHALL be preserved without requiring an explicit write

### Requirement: InboxSubscription is product subscription state, separate from realtime connection subscriptions

The `InboxSubscription` SHALL be product subscription state that controls durable inbox projection. It SHALL be separate from SignalR or live connection subscriptions, which decide only what a currently open browser connection receives in realtime. Changing a realtime connection subscription SHALL NOT change the `InboxSubscription`, and changing the `InboxSubscription` SHALL NOT be required to change what an open realtime connection receives.

#### Scenario: Realtime connection subscriptions do not change durable subscription state

- **WHEN** a SignalR or dashboard live subscription for a project is opened, closed, or changed
- **THEN** the project's `InboxSubscription` SHALL NOT change
- **AND** durable inbox projection SHALL continue to follow the `InboxSubscription`

#### Scenario: Durable subscription does not require an open connection

- **WHEN** a project's `InboxSubscription` is changed while no browser connection is open for that project
- **THEN** the change SHALL be persisted
- **AND** the change SHALL take effect for future inbox projection regardless of connection state

### Requirement: Project-scoped inbox subscription read and update HTTP API

The server SHALL provide a project-scoped HTTP API for the `InboxSubscription`. The API SHALL expose a read operation that returns the current enabled/disabled state for each of the four notification kinds, with a project that has no stored preferences reporting all four kinds as enabled. The API SHALL expose an update operation that sets the enabled/disabled state for the supported kinds. The update operation SHALL accept toggles keyed by `NotificationKind` and SHALL NOT accept raw CloudEvent type strings. Every subscription API operation SHALL be scoped to exactly one project and SHALL NOT read or mutate another project's subscription.

#### Scenario: Read returns the enabled state for each kind

- **WHEN** a client reads a project's inbox subscription
- **THEN** the response SHALL return the enabled/disabled state for each of `workflow_failed`, `approval_requested`, `issue_started`, and `issue_completed`
- **AND** the toggles SHALL be keyed by `NotificationKind`

#### Scenario: Read on a project with no stored preferences reports all four enabled

- **WHEN** a client reads a project's inbox subscription and the project has no stored preferences
- **THEN** the response SHALL report all four notification kinds as enabled

#### Scenario: Update sets the enabled state for the supported kinds

- **WHEN** a client updates a project's inbox subscription with a desired enabled/disabled state for one or more of the four kinds
- **THEN** the server SHALL persist the requested state
- **AND** a subsequent read SHALL return the updated state
- **AND** the update SHALL NOT accept keys other than the four supported `NotificationKind` values

#### Scenario: Subscription API operations cannot cross project boundaries

- **WHEN** a client performs a subscription API operation scoped to project A
- **THEN** the server SHALL NOT read or mutate project B's subscription
- **AND** the operation SHALL affect only project A's subscription

### Requirement: Web UI inbox subscription settings surface

The Web UI SHALL expose the four notification kind toggles under a project settings or inbox settings surface. Each toggle SHALL present a product-facing label describing the outcome (for example "Workflow failed", "Approval requested", "Issue started", "Issue completed") and SHALL NOT present raw event or CloudEvent type names. The UI SHALL load the currently persisted subscription state and SHALL persist every change through the inbox subscription HTTP API. The UI SHALL NOT mutate projection behavior through any path other than the subscription API.

#### Scenario: Settings surface exposes the four toggles with product-facing labels

- **WHEN** an operator opens the inbox subscription settings for a project
- **THEN** the UI SHALL render one toggle for each of the four notification kinds
- **AND** each toggle SHALL display a product-facing label
- **AND** the UI SHALL NOT display raw event or CloudEvent type names

#### Scenario: Settings surface reflects the persisted subscription state

- **WHEN** the inbox subscription settings are opened for a project
- **THEN** each toggle SHALL reflect the currently persisted enabled/disabled state for its kind
- **AND** a project with no stored preferences SHALL display all four toggles as enabled

#### Scenario: Toggle changes persist through the subscription API

- **WHEN** an operator changes a toggle in the settings surface
- **THEN** the UI SHALL persist the change through the inbox subscription HTTP API
- **AND** the UI SHALL NOT alter projection behavior through any other path
