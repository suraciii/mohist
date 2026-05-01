## MODIFIED Requirements

### Requirement: Changes section visible in more workflow stages

The Changes section SHALL be visible in stages beyond `build`, `check`, and `done`. The `DIFF_STAGES` set SHALL include at minimum `explore`, `plan`, `build`, `check`, and `done` — all stages where a worktree exists and commits may be present. Only `backlog` is excluded (no worktree exists).

#### Scenario: Changes visible in explore stage

- **WHEN** user views an issue in `explore` stage
- **AND** the issue has commits on its branch
- **THEN** the Changes section is rendered and visible

#### Scenario: Changes visible in plan stage

- **WHEN** user views an issue in `plan` stage
- **AND** the issue has commits on its branch
- **THEN** the Changes section is rendered and visible

#### Scenario: Changes not visible in draft stage

- **WHEN** user views an issue in `draft` stage
- **THEN** the Changes section is not rendered (no worktree exists yet)

#### Scenario: Changes section with no commits

- **WHEN** the issue is in an eligible stage but has no commits
- **THEN** the Changes section is hidden (same as current behavior — return null when both files and commits are empty)
