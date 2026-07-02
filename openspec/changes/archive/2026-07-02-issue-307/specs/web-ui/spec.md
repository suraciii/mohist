## ADDED Requirements

### Requirement: Stage-progression surfaces render only real pipeline stages

Workflow stage-progression surfaces — the stage bar (`WorkflowView`), the session timeline (`SessionTimeline`), the kanban card (`IssueCard`), and the issue detail page (`IssueDetailPage`) — SHALL render only the real executable pipeline stages (`plan`, `build`, `check`, `integrate`). They SHALL NOT synthesize, derive, or append a terminal "Done" stage cell to the rendered stage list or stage order. The `WorkflowStage.Done` enum member MAY be retained for compatibility, but SHALL NOT be added to any rendered stage list or stage order, and SHALL NOT be used to override an issue's displayed stage. Terminal state SHALL continue to be expressed as "all real stages green + issue status pill", not as an additional stage.

#### Scenario: Stage bar renders only the real pipeline stages
- **WHEN** a user views an issue whose workflow has progressed (including one whose workflow run has completed)
- **THEN** the stage bar SHALL render exactly `plan`, `build`, `check`, and `integrate`
- **AND** SHALL NOT render a synthesized "Done" stage cell

#### Scenario: Session timeline stage order excludes Done
- **WHEN** the session timeline derives its `stageOrder`
- **THEN** `stageOrder` SHALL contain only `plan`, `build`, `check`, and `integrate`
- **AND** SHALL NOT contain `done`

#### Scenario: Kanban card does not override stage to Done
- **WHEN** a kanban card renders the current stage for an issue whose status is `Done`
- **THEN** the card SHALL NOT override the displayed stage to `WorkflowStage.Done`
- **AND** SHALL derive the stage from the real pipeline stages only

#### Scenario: Issue detail omits a Done stage label
- **WHEN** the issue detail page renders its stage label map
- **THEN** it SHALL NOT include a `WorkflowStage.Done` label entry
- **AND** SHALL label only the real pipeline stages
