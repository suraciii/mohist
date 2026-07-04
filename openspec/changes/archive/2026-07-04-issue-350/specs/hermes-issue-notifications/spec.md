### Requirement: Key issue events produce Hermes webhook payloads

When Hermes issue notifications are configured, Mohist SHALL subscribe to the
approval-requested, workflow-failed, issue-completed, and issue-started
CloudEvents and produce Hermes webhook payloads for enabled notification types.

#### Scenario: Approval gate reached

- **WHEN** `com.mohist.workflow.stage.approval-requested` is published
- **AND** `approval_requested` is enabled
- **THEN** Mohist SHALL send a webhook payload containing issue number, issue
  title, approval stage, source event id/type, suggested action, and body
- **AND** the suggested action SHALL include the issue number

#### Scenario: Workflow failed

- **WHEN** `com.mohist.workflow.run.failed` is published
- **AND** `workflow_failed` is enabled
- **THEN** Mohist SHALL send a webhook payload containing issue number, issue
  title, failure reason, source event id/type, suggested action, and body
- **AND** the body SHALL NOT include a stack trace
- **AND** the suggested action SHALL include the issue number

#### Scenario: Issue completed

- **WHEN** `com.mohist.issue.completed` is published
- **AND** `issue_completed` is enabled
- **THEN** Mohist SHALL send a webhook payload containing issue number, issue
  title, source event id/type, suggested action, and body
- **AND** the suggested action SHALL include the issue number

### Requirement: Notification configuration controls delivery

Mohist SHALL bind Hermes notification configuration from
`Mohist:Notifications:Hermes`.

#### Scenario: Missing webhook URL disables outbound delivery

- **WHEN** `WebhookUrl` is missing or blank
- **THEN** Mohist SHALL NOT send any Hermes webhook request
- **AND** SHALL NOT load issue/workflow state for Hermes delivery

#### Scenario: Start notifications are off by default

- **WHEN** no `EnabledTypes` value is configured
- **THEN** `approval_requested`, `workflow_failed`, and `issue_completed` SHALL
  be enabled
- **AND** `issue_started` SHALL be disabled

#### Scenario: Start notifications can be enabled

- **WHEN** `issue_started` is included in `EnabledTypes`
- **AND** `com.mohist.issue.work-started` is published
- **THEN** Mohist SHALL send a start notification payload

#### Scenario: EnabledTypes filters notifications

- **WHEN** a notification type is not present in `EnabledTypes`
- **THEN** Mohist SHALL NOT send that notification

### Requirement: Hermes webhook transport is best-effort and signed when configured

Mohist SHALL send Hermes notifications as JSON over HTTP and SHALL NOT launch
local processes for delivery.

#### Scenario: Shared secret signs payload

- **WHEN** `Secret` is configured
- **THEN** Mohist SHALL include `X-Mohist-Signature` with an HMAC SHA-256
  signature over the JSON body

#### Scenario: Webhook delivery fails

- **WHEN** the Hermes webhook returns an error or is unreachable
- **THEN** Mohist SHALL log/swallow the error
- **AND** workflow or issue execution SHALL NOT be blocked

### Requirement: Hermes setup documentation is checked in

Mohist SHALL include user-facing documentation for connecting Hermes.

#### Scenario: User configures Hermes delivery

- **WHEN** a user reads the Hermes notification documentation
- **THEN** it SHALL include Mohist config keys, Hermes webhook subscription
  setup, secret alignment, a simple Hermes template, and an example payload/body
