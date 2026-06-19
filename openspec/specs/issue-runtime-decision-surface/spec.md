# OpenSpec Capability: issue-runtime-decision-surface

### Requirement: Issue Detail renders one primary runtime decision surface

Issue Detail SHALL render exactly one primary runtime decision surface near the top of the page, above the stage bar, task/check detail, sessions, and issue content sections. The surface SHALL present a single current-state summary drawn from the existing API facts (`workflowStage`, `health`, `approvalState`, `workflowTimeline`, `recovery`, `drift`, `convergence`, agent-status) and SHALL resolve to exactly one of: `running`, `queued`, `approval required`, `blocked`, `failed`, or `done`. No other region of Issue Detail SHALL present a competing primary current-state summary.

#### Scenario: Running workflow shows a single running summary

- **WHEN** a user opens Issue Detail for an issue whose workflow is actively executing
- **THEN** the page shows one decision surface near the top whose primary summary is `running`
- **AND** header pills, the stage bar, task rows, sessions, and the actions area do not each present a separate competing primary state answer

#### Scenario: Approval-required workflow shows a single approval-required summary

- **WHEN** a user opens Issue Detail for an issue with `approvalState.status === awaiting`
- **THEN** the page shows one decision surface whose primary summary is `approval required`
- **AND** the approval controls live inside that surface rather than only inside the workflow step list

#### Scenario: Queued workflow shows a single queued summary

- **WHEN** a user opens Issue Detail for an issue whose work is queued (for example, waiting for an active lease, runner availability, or capacity)
- **THEN** the page shows one decision surface whose primary summary is `queued`
- **AND** the surface explains that the workflow is waiting to start rather than presenting the issue as idle or failed

#### Scenario: Blocked or failed workflow shows a single blocked or failed summary

- **WHEN** a user opens Issue Detail for an issue whose latest attempt is blocked or failed according to the recovery projection
- **THEN** the page shows one decision surface whose primary summary is `blocked` or `failed`
- **AND** the failure or block reason and next action are exposed in the same surface

#### Scenario: Done workflow shows a single done summary

- **WHEN** a user opens Issue Detail for an issue whose workflow has completed
- **THEN** the page shows one decision surface whose primary summary is `done`
- **AND** the surface does not present stale running or approval-required guidance

### Requirement: Decision surface names the current task or check with its status

The decision surface SHALL name the current task or check and show its status whenever one exists, placing that information next to the required next action. When no individual task or check is current, the surface SHALL show a stage-level summary instead.

#### Scenario: Current running task is named in the surface

- **WHEN** the decision surface summary is `running`
- **AND** the recovery projection or stage timeline reports a current work item
- **THEN** the surface names that current task or check and shows its status
- **AND** the user does not need to open a session to identify what is running

#### Scenario: Current check is named in the surface

- **WHEN** the workflow is executing a check
- **THEN** the surface names the current check and shows its pass, fail, running, or pending status alongside the next action

#### Scenario: No current work item shows stage-level summary

- **WHEN** the decision surface summary is `running` but no individual task or check is currently active
- **THEN** the surface shows the current stage as the stage-level summary instead of naming a task

#### Scenario: Queued state does not fabricate a current task

- **WHEN** the decision surface summary is `queued`
- **THEN** the surface does not name a task as currently running
- **AND** it indicates what the workflow is waiting for when that reason is available

### Requirement: Decision surface exposes the context-specific next action

The decision surface SHALL expose the required next action contextually, choosing among approval, recovery, inspection, start, and wait guidance based on the same API facts that drive the summary. Action availability SHALL follow the backend recovery projection and workflow available actions, not issue-level heuristics alone. The surface SHALL NOT scatter these actions across header pills, the workflow step list, and a separate actions card as competing primary answers.

#### Scenario: Approval actions appear in the surface

- **WHEN** the decision surface summary is `approval required`
- **THEN** the approve and send-back actions appear inside the surface
- **AND** the inline approval panel in the workflow step list does not serve as the primary place the user must look to approve

#### Scenario: Recovery actions appear in the surface

- **WHEN** the decision surface summary is `blocked`, `failed`, or `interrupted`
- **THEN** retry, resume, rerun, and stop actions appear inside the surface according to the recovery projection's allowed actions
- **AND** the surface does not enable actions the backend projection does not allow

#### Scenario: Safe inspection links appear in the surface

- **WHEN** the workflow offers safe inspection of changes, transcripts, or failure evidence
- **THEN** the surface exposes inspection links such as View files or View transcript as the context-specific next step
- **AND** those links are reachable from the surface without first locating them in a separate panel

#### Scenario: Start and wait guidance appear in the surface

- **WHEN** the issue is in backlog and start-eligible, or the workflow is queued or running without user action required
- **THEN** the surface exposes Start, or wait guidance, as the context-specific next step
- **AND** start eligibility and runner availability reasons are shown in the surface

#### Scenario: Action availability follows the API projection

- **WHEN** the decision surface renders its actions
- **THEN** each enabled action corresponds to an action present in the recovery projection or workflow available actions
- **AND** unavailable actions are not inferred from issue status or health alone

### Requirement: Sessions and logs are supporting evidence, not primary state

The decision surface SHALL answer whether the user must wait, approve, request changes, or recover without requiring the user to open a session or read logs. Sessions and logs SHALL remain reachable from Issue Detail as supporting evidence.

#### Scenario: Decision does not require opening a session

- **WHEN** a user reads the decision surface for a running, approval-required, blocked, or failed issue
- **THEN** the surface alone communicates the required next action
- **AND** the user is not required to inspect a session to decide what to do

#### Scenario: Sessions remain reachable as evidence

- **WHEN** a user wants supporting detail after reading the decision surface
- **THEN** session and log entry points remain available lower on Issue Detail
- **AND** those entry points are presented as supporting evidence rather than as the primary way to infer current state

### Requirement: Runtime transport notices are routed away from inline issue content

Runtime infrastructure notices — including connection-disconnect messages, transport errors, and runner-drop indicators — SHALL render in Logs, Activity, a toast, or a debug area. They SHALL NOT render as plain inline content between Description, Commits, Comments, or other issue content sections.

#### Scenario: Connection disconnect is not shown inline between issue sections

- **WHEN** a live-events connection disconnects while a user is viewing Issue Detail
- **THEN** the disconnect notice is surfaced through a toast, Logs, Activity, or a debug area
- **AND** it does not appear as plain inline text between the Commits and Comments sections or between any other issue content sections

#### Scenario: Runner-drop indicators are not inline issue content

- **WHEN** a runner becomes unavailable while a user is viewing Issue Detail
- **THEN** the runner-drop indicator is routed to the same non-inline surface as other runtime transport notices
- **AND** it does not appear as plain inline text within Description, Commits, or Comments

#### Scenario: Issue content sections contain only issue content

- **WHEN** a user scans Description, Commits, Comments, and other issue content sections
- **THEN** those sections contain only issue content and review material
- **AND** runtime transport notices are confined to Logs, Activity, toasts, or a debug area

### Requirement: Decision surface has regression test coverage

The decision surface SHALL have regression test coverage that protects the running summary, the approval-required summary, and the disconnected-runtime-notice routing at minimum.

#### Scenario: Running summary rendering is covered

- **WHEN** the decision surface tests run against a running-workflow issue fixture
- **THEN** they verify the single `running` summary renders near the top and names the current task or check

#### Scenario: Approval-required summary rendering is covered

- **WHEN** the decision surface tests run against an approval-awaiting issue fixture
- **THEN** they verify the single `approval required` summary renders with approval actions inside the surface

#### Scenario: Disconnected runtime notice routing is covered

- **WHEN** the decision surface tests run against a disconnected-transport fixture
- **THEN** they verify the disconnect notice is routed to a toast, Logs, Activity, or a debug area
- **AND** they verify the notice does not render as plain inline content between issue content sections

### Requirement: Decision surface uses a restrained background with a colored edge accent

The decision surface SHALL use a neutral (white/paper) background instead of a full-surface colored background. It SHALL convey the current runtime state using a colored accent on one edge (for example a left colored border) together with the status text and action buttons. Each runtime state (`running`, `queued`, `approval required`, `blocked`, `failed`, `done`) SHALL remain visually distinguishable from the others through the combination of edge accent and status text, without relying on a full-surface colored fill. The surface SHALL NOT stack multiple full-surface colored blocks that compete for the visual center of the first screen.

#### Scenario: Decision surface uses a neutral background with an edge accent

- **WHEN** Issue Detail renders the decision surface for any runtime state
- **THEN** the surface SHALL render a neutral background rather than a full-surface colored fill
- **AND** the surface SHALL render a colored accent on one edge to convey the state

#### Scenario: Each runtime state remains visually distinguishable

- **WHEN** Issue Detail renders the decision surface across `running`, `queued`, `approval required`, `blocked`, `failed`, and `done` states
- **THEN** each state SHALL be visually distinguishable from the others via its edge accent and status text
- **AND** distinguishability SHALL NOT depend on a full-surface colored background

#### Scenario: First screen avoids competing full-surface colored blocks

- **WHEN** Issue Detail renders the decision surface alongside other first-screen regions (for example convergence or interrupted indicators)
- **THEN** the surface SHALL NOT render as a full-surface colored block that competes for the visual center
- **AND** the first screen SHALL avoid stacking multiple full-surface colored blocks
