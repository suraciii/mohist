# issue-detail-primary-action Specification

## Requirements

### Requirement: Single Runtime Action Surface

The issue detail page SHALL render workflow runtime write controls through one runtime decision surface.

#### Scenario: Approval is awaiting

- **WHEN** the workflow projection reports approval is awaiting
- **THEN** the runtime decision surface exposes Approve and Send back according to available backend actions
- **AND** the embedded workflow evidence view does not render its own approval or request-changes controls on the detail page

#### Scenario: Workflow is recoverable or failed

- **WHEN** recovery or timeline projections expose retry, resume, rerun, start, or stop actions
- **THEN** the runtime decision surface renders those actions using the shared issue-detail mutation state
- **AND** right-rail cards do not duplicate the same mutation error message

### Requirement: Backlog Readiness Uses Start

Ready backlog issues SHALL be classified as queued/ready-to-start rather than running.

#### Scenario: Backlog is ready

- **WHEN** an issue is in backlog, has no blocker, and can start
- **THEN** Start is the enabled primary runtime action
- **AND** Stop is not rendered for that issue

#### Scenario: Backlog is waiting

- **WHEN** an issue is in backlog and has a readiness, runner, or capacity blocker
- **THEN** Start is visible but disabled
- **AND** the disabled reason describes the blocker

### Requirement: No Enabled No-Op Runtime Actions

Runtime actions SHALL NOT be enabled unless the surface has a handler or concrete navigation target.

#### Scenario: Inspect is offered by projection without a detail-page destination

- **WHEN** the projection offers inspect
- **THEN** View transcript is disabled on the runtime decision surface
- **AND** clicking it cannot perform a no-op enabled action
