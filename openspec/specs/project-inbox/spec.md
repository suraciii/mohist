### Requirement: InboxItem is a project-scoped durable notification fact addressed to the local operator

An `InboxItem` SHALL be a durable message generated from a domain event. Every `InboxItem` SHALL belong to exactly one project and SHALL be addressed to that project's local operator. The model SHALL NOT require a real user id; the recipient is implicit and local. An `InboxItem` SHALL persist independently of any browser or live connection so it remains available when the browser is closed. Each `InboxItem` SHALL carry a `NotificationKind` drawn from exactly the MVP set: `workflow_failed`, `approval_requested`, `issue_started`, and `issue_completed`. Each `InboxItem` that refers to issue work SHALL carry enough issue identity (issue number plus issue title or summary) to open the issue from the inbox.

#### Scenario: An inbox item belongs to exactly one project

- **WHEN** an inbox item is created for a project
- **THEN** the item SHALL record exactly one owning project
- **AND** the item SHALL NOT be addressable without a project scope

#### Scenario: Recipient is the implicit local operator

- **WHEN** an inbox item is stored
- **THEN** the item SHALL be addressed to that project's local operator
- **AND** the item SHALL NOT require or store a real user id

#### Scenario: Inbox items survive the browser being closed

- **WHEN** a domain event produces an inbox item and no browser is open against the project
- **THEN** the item SHALL be durably persisted on the server
- **AND** the item SHALL be retrievable later when an operator opens the project inbox

#### Scenario: Notification kind is one of the four MVP kinds

- **WHEN** an inbox item is created
- **THEN** its `NotificationKind` SHALL be one of `workflow_failed`, `approval_requested`, `issue_started`, or `issue_completed`

#### Scenario: Item carries enough issue identity to deep-link

- **WHEN** an inbox item that refers to issue work is stored
- **THEN** the item SHALL carry the issue number
- **AND** the item SHALL carry the issue title or summary
- **AND** the carried identity SHALL be sufficient to open the issue from the inbox

### Requirement: Server-side projection produces one inbox item per authoritative event by kind

The server SHALL produce inbox items exclusively through a server-side projection over authoritative issue and workflow events. The projection SHALL map exactly these events to exactly these notification kinds: a `WorkflowRunFailed` event SHALL produce one `workflow_failed` item, a `StageApprovalRequested` event SHALL produce one `approval_requested` item, an `IssueWorkStarted` event SHALL produce one `issue_started` item, and an `IssueWorkCompleted` event SHALL produce one `issue_completed` item. Each produced item SHALL be placed in the project that owns the source issue or workflow run.

#### Scenario: WorkflowRunFailed produces one workflow_failed item

- **WHEN** the projection observes a `WorkflowRunFailed` event for a workflow run owned by a project
- **THEN** the projection SHALL create exactly one `workflow_failed` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: StageApprovalRequested produces one approval_requested item

- **WHEN** the projection observes a `StageApprovalRequested` event for a workflow run owned by a project
- **THEN** the projection SHALL create exactly one `approval_requested` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: IssueWorkStarted produces one issue_started item

- **WHEN** the projection observes an `IssueWorkStarted` event for an issue owned by a project
- **THEN** the projection SHALL create exactly one `issue_started` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: IssueWorkCompleted produces one issue_completed item

- **WHEN** the projection observes an `IssueWorkCompleted` event for an issue owned by a project
- **THEN** the projection SHALL create exactly one `issue_completed` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: No other event produces an inbox item in the MVP

- **WHEN** the projection observes any event other than `WorkflowRunFailed`, `StageApprovalRequested`, `IssueWorkStarted`, or `IssueWorkCompleted`
- **THEN** the projection SHALL NOT create an inbox item

### Requirement: Inbox delivery is decided only by server-side projection

Only the server-side projection SHALL decide that a domain event becomes an inbox item. The runner SHALL NOT contain any inbox-delivery logic; the runner SHALL only report facts through the existing event stream. The Web client SHALL only display and refresh inbox data via the inbox API; it SHALL NOT decide that an event becomes an inbox item. Existing live connection subscriptions (dashboard and SignalR) SHALL remain transport or presentation state and SHALL NOT be the source of inbox truth.

#### Scenario: Runner reports facts but does not decide delivery

- **WHEN** the runner observes a condition that would justify a notification
- **THEN** the runner SHALL NOT create or deliver an inbox item
- **AND** the runner SHALL only report facts through the existing event stream

#### Scenario: Web client displays inbox data but does not decide delivery

- **WHEN** the Web client receives an event over a live connection
- **THEN** the Web client SHALL NOT synthesize or persist an inbox item
- **AND** the Web client SHALL only read and mutate inbox items through the inbox API

#### Scenario: Live subscriptions are not the source of inbox truth

- **WHEN** a live dashboard or SignalR subscription is active for a project
- **THEN** that subscription SHALL be treated as transport or presentation state only
- **AND** the durable inbox truth SHALL reside in the server-side projection and its read model

### Requirement: Projection is idempotent by source event

The projection SHALL be idempotent by source event. Replaying or retrying the handling of a given source event SHALL NOT create additional inbox items for that event. The projection SHALL deduplicate so that at most one inbox item exists per source event.

#### Scenario: Event replay does not duplicate an item

- **WHEN** the same source event is replayed to the projection after an item already exists for it
- **THEN** the projection SHALL NOT create a second inbox item for that event
- **AND** exactly one inbox item SHALL remain for that source event

#### Scenario: Repeated event handling does not duplicate an item

- **WHEN** event handling for a given source event is retried or redelivered
- **THEN** the projection SHALL NOT create additional inbox items for that event
- **AND** the inbox SHALL continue to contain at most one item per source event

### Requirement: Projection enforces strict project isolation

Inbox projection and storage SHALL be project-isolated. An inbox item created from a source event SHALL appear only in the project that owns that source issue or workflow run. Items produced in one project SHALL NOT appear in another project's inbox under any condition.

#### Scenario: An item is created only in the owning project

- **WHEN** a source event is owned by project A
- **THEN** the resulting inbox item SHALL be scoped to project A
- **AND** the item SHALL NOT be visible in any other project's inbox

#### Scenario: One project's items never leak into another project

- **WHEN** project A and project B each have inbox items
- **THEN** listing project A's inbox SHALL return only items scoped to project A
- **AND** listing project B's inbox SHALL return only items scoped to project B

### Requirement: Inbox item text is product-facing with issue deep-link identity

Each inbox item SHALL present a product-facing reason rather than a raw event type. A `workflow_failed` item SHALL read, for example, "Issue #42 workflow failed"; an `approval_requested` item SHALL read, for example, "Issue #42 needs approval"; an `issue_started` item SHALL read, for example, "Issue #42 started"; and an `issue_completed` item SHALL read, for example, "Issue #42 completed". Each item SHALL expose the issue number and issue title or summary so a user can open the issue directly from the inbox.

#### Scenario: workflow_failed item uses product-facing text

- **WHEN** a `workflow_failed` inbox item is presented
- **THEN** the item text SHALL be product-facing (for example "Issue #42 workflow failed")
- **AND** the text SHALL NOT expose the raw event type name

#### Scenario: approval_requested item uses product-facing text

- **WHEN** an `approval_requested` inbox item is presented
- **THEN** the item text SHALL be product-facing (for example "Issue #42 needs approval")
- **AND** the text SHALL NOT expose the raw event type name

#### Scenario: issue_started and issue_completed items use product-facing text

- **WHEN** an `issue_started` or `issue_completed` inbox item is presented
- **THEN** the item text SHALL be product-facing (for example "Issue #42 started" or "Issue #42 completed")
- **AND** the text SHALL NOT expose the raw event type name

#### Scenario: Item exposes issue number and title or summary

- **WHEN** an inbox item that refers to issue work is presented
- **THEN** the item SHALL expose the issue number
- **AND** the item SHALL expose the issue title or summary

### Requirement: Project inbox HTTP API

The server SHALL provide a project-scoped inbox HTTP API. The API SHALL expose a list operation that returns project inbox items, each including notification kind, issue number, issue title or summary, creation time, and read/unread state. The list SHALL be ordered most-recent-first by creation time and SHALL exclude archived or dismissed items by default. The API SHALL support marking exactly one item read, marking all items in the project read, and archiving or dismissing exactly one item. Every inbox API operation SHALL be scoped to a single project and SHALL NOT read or mutate another project's items.

#### Scenario: List returns the required item fields ordered most-recent-first

- **WHEN** a client requests the project inbox list for a project that has items
- **THEN** the response SHALL return each item's notification kind, issue number, issue title or summary, creation time, and read/unread state
- **AND** the items SHALL be ordered most-recent-first by creation time
- **AND** the list SHALL exclude archived or dismissed items by default

#### Scenario: Mark one item read

- **WHEN** a client requests marking a single inbox item read within a project
- **THEN** the server SHALL set that item's read state to read
- **AND** SHALL NOT alter the read state of any other item

#### Scenario: Mark all project items read

- **WHEN** a client requests marking all items read for a project
- **THEN** the server SHALL set every non-archived item in that project to read
- **AND** SHALL NOT alter the read state of items in any other project

#### Scenario: Archive or dismiss one item

- **WHEN** a client requests archiving or dismissing a single inbox item within a project
- **THEN** the server SHALL mark that item as archived or dismissed
- **AND** the item SHALL be excluded from the default inbox list thereafter
- **AND** SHALL NOT affect any other item

#### Scenario: API operations cannot cross project boundaries

- **WHEN** a client performs any inbox API operation scoped to project A against an item that belongs to project B
- **THEN** the server SHALL NOT read or mutate project B's item
- **AND** the operation SHALL be rejected as not found or otherwise not exposed to project A

### Requirement: Web UI project inbox page

The Web UI SHALL provide a project inbox route or page that lists the project's inbox items with read/unread presentation and a link from each item back to its issue. The page SHALL render an explicit empty state for a project that has no inbox items. The page SHALL obtain and refresh inbox data solely through the inbox HTTP API and SHALL NOT decide that an event becomes an inbox item.

#### Scenario: Project inbox page renders the item list

- **WHEN** an operator opens the project inbox page for a project that has items
- **THEN** the page SHALL render the inbox items with their notification kind, issue identity, and read/unread presentation
- **AND** each item SHALL offer a link to open the issue it refers to

#### Scenario: Project inbox page shows an explicit empty state

- **WHEN** an operator opens the project inbox page for a project that has no inbox items
- **THEN** the page SHALL render an explicit empty state
- **AND** SHALL NOT render a broken or ambiguous list

#### Scenario: Read and unread items are visually distinguishable

- **WHEN** the project inbox page renders a mix of read and unread items
- **THEN** the page SHALL present unread items as distinct from read items

#### Scenario: Page drives inbox state only through the API

- **WHEN** the project inbox page displays or mutates inbox data
- **THEN** the page SHALL read and refresh inbox data solely through the inbox HTTP API
- **AND** SHALL NOT locally synthesize or persist inbox items from events
