## MODIFIED Requirements

### Requirement: REQ-WUI-005 Integrate progress is visible in Issue Detail

Issue Detail progress surfaces SHALL render Integrate from persisted task and check state so users can see which integration step is running, which steps completed, and whether final verification passed or failed.

#### Scenario: Integrate tasks are visible while running

- **WHEN** the active stage is Integrate and task state is available
- **THEN** Issue Detail SHALL display `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as separate tasks in order
- **AND** it SHALL show current running or completed status for each task

#### Scenario: Final health is shown as a check, not a task

- **WHEN** Integrate check state includes `health:integrate`
- **THEN** Issue Detail SHALL render that item in the checks section rather than the task list
- **AND** it SHALL show pass/fail state and diagnostic evidence separately from Integrate task progress
