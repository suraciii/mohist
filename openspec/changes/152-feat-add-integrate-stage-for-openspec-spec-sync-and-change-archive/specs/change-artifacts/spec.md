## MODIFIED Requirements

### Requirement: OpenSpec change archive belongs to Integrate
The system SHALL treat OpenSpec change archive as an Integrate action, distinct from issue archive and worktree cleanup.

#### Scenario: Archive after spec sync
- **WHEN** Integrate successfully synchronizes approved delta specs
- **THEN** the active OpenSpec change is moved to `openspec/changes/archive/YYYY-MM-DD-<change>/`
- **AND** the archive preserves proposal, design, specs, tasks, review evidence, session memories, and integration summary when present

#### Scenario: Issue archive remains separate
- **WHEN** a user archives a completed issue or cleans a worktree
- **THEN** that action does not redefine or repeat OpenSpec change archive semantics
