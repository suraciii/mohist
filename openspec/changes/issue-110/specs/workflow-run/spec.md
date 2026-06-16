## ADDED Requirements

### Requirement: Workflow retry validates session context health before resuming

The workflow retry path SHALL inspect the context window usage of the agent session associated with the failed work item before allowing retry execution. If the session's context usage exceeds 90%, retry SHALL be blocked and the WorkflowRun SHALL record the blocking reason as session context capacity. If usage is between 80% and 90%, a warning SHALL be logged but retry SHALL proceed. Below 80%, retry SHALL proceed normally.

#### Scenario: Retry proceeds with healthy session context
- **WHEN** a Build task fails and the associated agent session has context usage at 45%
- **AND** a user triggers retry for that task
- **THEN** retry SHALL proceed normally
- **AND** the task SHALL be reset and queued for re-execution using the existing session

#### Scenario: Retry blocked by exhausted session context
- **WHEN** a Build task fails and the associated agent session has context usage at 92%
- **AND** a user triggers retry for that task
- **THEN** retry SHALL be rejected
- **AND** the WorkflowRun SHALL record a blocking reason indicating "Session context near capacity (92%)"
- **AND** the error response SHALL suggest Compact or Reset before retrying

#### Scenario: Retry succeeds after session recovery
- **WHEN** a previously blocked retry is re-attempted after the user compacts the session from 92% to 50%
- **THEN** the session health check SHALL pass
- **AND** retry SHALL proceed normally

### Requirement: Workflow session list surfaces context health state

Workflow views that display session lists (workflow run detail, stage views with linked sessions) SHALL surface context health indicators for each session. The health data SHALL be derived from the session's persisted context window metrics.

#### Scenario: Workflow session list shows health indicators
- **WHEN** a workflow view renders a list of agent sessions for the current workflow run
- **THEN** each session entry SHALL show a context usage indicator with current percentage and color

#### Scenario: Workflow context menu provides Compact and Reset actions
- **WHEN** a user opens the context menu for a session in a workflow view
- **THEN** the menu SHALL include Compact and Reset options when the session is not actively running
- **AND** selecting these options SHALL trigger the corresponding API calls

### Requirement: Session health is evaluated at task dispatch time

Before dispatching a task that uses an existing agent session, the workflow executor SHALL evaluate context health. If usage exceeds 90%, the task SHALL NOT be dispatched and the stage SHALL record a blocking reason. If usage is between 80-90%, a warning SHALL be logged but dispatch SHALL proceed.

#### Scenario: Task dispatch blocked at critical usage
- **WHEN** the workflow executor attempts to dispatch a task using a session with 93% context usage
- **THEN** the task SHALL NOT be dispatched
- **AND** the stage SHALL record "Session context near capacity" as a blocking reason
- **AND** the user SHALL be notified to compact or reset before continuing

#### Scenario: Task dispatch proceeds with warning at elevated usage
- **WHEN** the workflow executor dispatches a task using a session with 85% context usage
- **THEN** the task SHALL be dispatched normally
- **AND** a warning SHALL be logged indicating elevated context usage
- **AND** the stage SHALL continue execution

#### Scenario: Task dispatch proceeds normally at healthy usage
- **WHEN** the workflow executor dispatches a task using a session with 40% context usage
- **THEN** the task SHALL be dispatched normally
- **AND** no warning or blocking SHALL occur
