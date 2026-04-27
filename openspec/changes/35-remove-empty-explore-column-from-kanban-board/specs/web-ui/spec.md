## MODIFIED Requirements

### Requirement: Kanban board displays only workflow stages

The Kanban board `STAGES` array SHALL contain exactly the stages that issues can occupy in the workflow. The Explore stage SHALL NOT appear as a Kanban column because explore sessions are managed on a separate `/explore` page and are never represented as issues.

The displayed stages SHALL be, in order: Draft, Plan, Build, Review, Done.

#### Scenario: Kanban board shows five columns

- **WHEN** the Kanban board renders
- **THEN** it displays exactly five columns: Draft, Plan, Build, Review, Done
- **AND** no Explore column is shown

#### Scenario: Issues with explore stage are not displayed in Kanban

- **WHEN** an issue exists with `stage='explore'`
- **THEN** it does not appear in any Kanban column
- **AND** it is only accessible via the `/explore` page
