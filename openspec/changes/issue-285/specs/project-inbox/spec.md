## MODIFIED Requirements

### Requirement: Server-side projection produces one inbox item per authoritative event by kind

The server SHALL produce inbox items exclusively through a server-side projection over authoritative issue and workflow events. The projection SHALL map exactly these events to exactly these notification kinds: a `WorkflowRunFailed` event SHALL produce one `workflow_failed` item, a `StageApprovalRequested` event SHALL produce one `approval_requested` item, an `IssueWorkStarted` event SHALL produce one `issue_started` item, and an `IssueWorkCompleted` event SHALL produce one `issue_completed` item. Each produced item SHALL be placed in the project that owns the source issue or workflow run. The projection SHALL create an inbox item for a mapped event only when the resulting notification kind is enabled in the owning project's `InboxSubscription`; when the kind is disabled for that project, the projection SHALL NOT create an inbox item for that event. A project with no stored `InboxSubscription` SHALL be treated as having all four kinds enabled, preserving the existing MVP behavior.

#### Scenario: WorkflowRunFailed produces one workflow_failed item when that kind is enabled

- **WHEN** the projection observes a `WorkflowRunFailed` event for a workflow run owned by a project
- **AND** `workflow_failed` is enabled in that project's `InboxSubscription`
- **THEN** the projection SHALL create exactly one `workflow_failed` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: StageApprovalRequested produces one approval_requested item when that kind is enabled

- **WHEN** the projection observes a `StageApprovalRequested` event for a workflow run owned by a project
- **AND** `approval_requested` is enabled in that project's `InboxSubscription`
- **THEN** the projection SHALL create exactly one `approval_requested` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: IssueWorkStarted produces one issue_started item when that kind is enabled

- **WHEN** the projection observes an `IssueWorkStarted` event for an issue owned by a project
- **AND** `issue_started` is enabled in that project's `InboxSubscription`
- **THEN** the projection SHALL create exactly one `issue_started` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: IssueWorkCompleted produces one issue_completed item when that kind is enabled

- **WHEN** the projection observes an `IssueWorkCompleted` event for an issue owned by a project
- **AND** `issue_completed` is enabled in that project's `InboxSubscription`
- **THEN** the projection SHALL create exactly one `issue_completed` inbox item
- **AND** the item SHALL be placed in that project's inbox

#### Scenario: Projection skips a mapped event whose kind is disabled for the owning project

- **WHEN** the projection observes a mapped event whose notification kind is disabled in the owning project's `InboxSubscription`
- **THEN** the projection SHALL NOT create an inbox item for that event
- **AND** the projection SHALL NOT alter any existing inbox item

#### Scenario: Projection treats a project with no stored subscription as all-enabled

- **WHEN** the projection observes a mapped event for a project that has no stored `InboxSubscription`
- **THEN** the projection SHALL behave as if all four notification kinds are enabled
- **AND** the projection SHALL create the corresponding inbox item

#### Scenario: No other event produces an inbox item in the MVP

- **WHEN** the projection observes any event other than `WorkflowRunFailed`, `StageApprovalRequested`, `IssueWorkStarted`, or `IssueWorkCompleted`
- **THEN** the projection SHALL NOT create an inbox item
