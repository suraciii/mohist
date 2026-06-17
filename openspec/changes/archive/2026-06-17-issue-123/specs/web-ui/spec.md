## ADDED Requirements

### Requirement: Issue Detail routes its primary runtime answer through the decision surface

Issue Detail SHALL present the `issue-runtime-decision-surface` as the single primary answer to the current workflow state and required next action, rendered above the stage bar, task/check detail, sessions, and issue content sections. The header stage and health pills, the workflow step list, the inline approval panel, the right-hand actions card, and the convergence/drift/interrupted cards SHALL NOT each serve as the primary competing state answer; they SHALL remain available as supporting detail beneath the surface.

#### Scenario: Primary answer appears above scattered panels

- **WHEN** a user opens Issue Detail for an active, queued, approval-required, blocked, failed, or done issue
- **THEN** the page renders the decision surface above the stage bar, task/check detail, sessions, and content sections
- **AND** the header pills, workflow step list, inline approval panel, and actions card do not present a separate competing primary state summary

#### Scenario: Existing detail panels remain as supporting detail

- **WHEN** the decision surface is rendered
- **THEN** the stage bar, task/check rows, sessions, drift and convergence cards, and issue content sections remain visible beneath the surface as supporting evidence
- **AND** those regions are not removed from Issue Detail

### Requirement: Runtime transport notices do not render as inline issue content

Issue Detail SHALL NOT render runtime transport notices — such as connection-disconnect messages, transport errors, or runner-drop indicators — as plain inline content between Description, Commits, Comments, or other issue content sections. Such notices SHALL be confined to Logs, Activity, a toast, or a debug area.

#### Scenario: Transport notices stay out of issue content sections

- **WHEN** a runtime transport notice occurs while Issue Detail is open
- **THEN** the notice is rendered in Logs, Activity, a toast, or a debug area
- **AND** it does not appear as plain inline text between any issue content section on Issue Detail
