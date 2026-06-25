## ADDED Requirements

### Requirement: Archived issue detail page renders workflow execution history

The Web UI issue detail page SHALL render the full workflow execution history for an archived issue from its preserved workflow run reference. Archiving SHALL NOT cause the detail page to hide, omit, or fail to load the workflow timeline, artifacts, events, feedback, commits, diffs, or execution context. The archived detail page SHALL use the same rendering path as a non-archived issue detail page, differing only in visibility/list placement, not in history access.

#### Scenario: Archived issue detail shows the workflow timeline

- **WHEN** a user opens the detail page of a `Done` issue that was archived after completing workflow run `wr_1`
- **THEN** the page SHALL render the workflow timeline for `wr_1`
- **AND** the page SHALL display the `archivedAt` state without removing execution history

#### Scenario: Archived issue detail shows artifacts and feedback

- **WHEN** a user opens the detail page of an archived issue
- **THEN** the page SHALL render the artifacts, events, and feedback produced by the preserved workflow run
- **AND** no history section that renders for a non-archived `Done` issue SHALL be hidden for the archived issue

#### Scenario: Archived detail does not show an active workflow control surface

- **WHEN** a user views an archived issue detail page whose `workflowRunId` is preserved
- **THEN** the page SHALL NOT present active-workflow controls (start/stop/retry) as if the workflow were running
- **AND** any workflow status indicator SHALL reflect the archived/`Done` state
