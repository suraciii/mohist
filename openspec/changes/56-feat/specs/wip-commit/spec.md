## ADDED Requirements

### Requirement: WIP commit on agent timeout
The system SHALL create a WIP (Work-In-Progress) git commit in the issue worktree before killing a timed-out agent process, preserving all file modifications made during the task.

#### Scenario: Timeout triggers WIP commit
- **WHEN** a coder agent task execution exceeds the per-task timeout
- **AND** the worktree has uncommitted changes (modified, added, or deleted files)
- **THEN** the system SHALL execute `git add -A` followed by `git commit -m "WIP: T-XXX timeout (attempt N)"` in the worktree before killing the agent process
- **AND** the task failure category SHALL be recorded as `timeout` with `wipCommitted: true`

#### Scenario: Timeout with no changes
- **WHEN** a coder agent task execution exceeds the per-task timeout
- **AND** the worktree has no uncommitted changes
- **THEN** the system SHALL skip WIP commit
- **AND** the task failure category SHALL be recorded as `timeout` with `wipCommitted: false`

#### Scenario: WIP commit fails
- **WHEN** the system attempts to create a WIP commit during timeout handling
- **AND** the git commit fails (e.g., lock file, permission error)
- **THEN** the system SHALL log the commit failure as a warning
- **AND** SHALL proceed to kill the agent process regardless
- **AND** the task failure category SHALL be recorded as `timeout` with `wipCommitted: false`

### Requirement: WIP commit format
WIP commits SHALL follow a consistent format that encodes task identity for downstream recovery.

#### Scenario: WIP commit message format
- **WHEN** the system creates a WIP commit for task T-003 on attempt 1
- **THEN** the commit message SHALL be `"WIP: T-003 timeout (attempt 1)"`
- **AND** the commit author SHALL be `mohist-wip <mohist@wip>`

#### Scenario: Multiple WIP commits for same task
- **WHEN** task T-003 times out a second time after retry (attempt 2)
- **AND** a prior `WIP: T-003 timeout (attempt 1)` commit already exists
- **THEN** the system SHALL create an additional WIP commit with message `"WIP: T-003 timeout (attempt 2)"`
- **AND** the prior WIP commit SHALL be preserved (not amended)

### Requirement: WIP commit query
The system SHALL support querying the most recent WIP commit for a given task in a worktree.

#### Scenario: Find WIP commit for a task
- **WHEN** the system queries for WIP commits of task T-003 in a worktree
- **THEN** the system SHALL return the most recent commit whose message matches `"WIP: T-003 timeout*"`
- **AND** SHALL include the commit hash, message, list of changed files, and diff summary

#### Scenario: No WIP commit exists
- **WHEN** the system queries for WIP commits of task T-003 in a worktree
- **AND** no commit with message matching `"WIP: T-003 timeout*"` exists
- **THEN** the system SHALL return `null`

### Requirement: WIP commit preservation on approval
WIP commits SHALL be treated as regular implementation commits when the user approves the final result.

#### Scenario: User approves with WIP commits present
- **WHEN** the user approves the build stage result
- **AND** the worktree branch contains WIP commits
- **THEN** WIP commits SHALL be preserved as-is in the branch history
- **AND** the `mergeBack()` operation SHALL include WIP commits in the merge
- **AND** WIP commits SHALL NOT be squashed or amended during the merge process
