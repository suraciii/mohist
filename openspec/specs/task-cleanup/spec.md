# OpenSpec Capability: task-cleanup

### Requirement: Task completion requires a clean git worktree

No workflow task SHALL be marked completed while the task workspace contains uncommitted changes. The runner SHALL verify `git status --porcelain` returns empty output before reporting the task as completed. This invariant applies to agent-backed tasks and deterministic action tasks alike.

If the worktree-probe git commands themselves fail (corrupted worktree, missing git binary, permission error), the runner SHALL fail the task with structured dirty-worktree evidence naming the probe failure; the runner SHALL NOT silently treat an unevaluable worktree as clean. The single exception is the standard "not a git repository" stderr from `git rev-parse --is-inside-work-tree`, which is treated as the legitimate plain-tmpdir / non-worktree path used by some test fixtures.

#### Scenario: Agent-backed task with clean worktree completes normally

- **WHEN** an agent-backed task finishes execution
- **AND** `git status --porcelain` returns empty output in the task workspace
- **THEN** the task SHALL be marked completed
- **AND** the runner SHALL report success to WorkflowRun

#### Scenario: Deterministic action leaves clean worktree

- **WHEN** a deterministic action completes its work
- **AND** `git status --porcelain` returns empty output in the task workspace
- **THEN** the action SHALL report success
- **AND** the task SHALL be marked completed

#### Scenario: Deterministic action leaves dirty worktree

- **WHEN** a deterministic action completes its work
- **AND** `git status --porcelain` returns non-empty output
- **THEN** the action SHALL report failure with structured dirty-worktree evidence
- **AND** the task SHALL NOT be marked completed

#### Scenario: Worktree-probe failure produces structured evidence

- **WHEN** the runner's worktree-probe git commands fail with a non-"not a git repository" error
- **THEN** the runner SHALL fail the task with structured dirty-worktree evidence naming the probe failure
- **AND** the task SHALL NOT be marked completed

### Requirement: Agent cleanup loop recovers uncommitted changes

When an agent-backed task returns with uncommitted changes, the runner SHALL enter a bounded cleanup loop using the same agent session. Each cleanup attempt SHALL send a follow-up prompt instructing the agent to inspect uncommitted changes and either commit task-related changes or revert changes that should not be kept.

#### Scenario: Cleanup prompt explicitly constrains agent behavior

- **WHEN** the runner sends a cleanup follow-up prompt to the agent
- **THEN** the prompt SHALL explicitly instruct the agent NOT to start new work
- **AND** the prompt SHALL explicitly instruct the agent NOT to push to any remote
- **AND** the prompt SHALL instruct the agent to commit task-related changes or revert unrelated changes
- **AND** the prompt SHALL instruct the agent to report a summary and commit sha or no-change result

#### Scenario: Cleanup succeeds on first attempt

- **WHEN** an agent-backed task returns with uncommitted changes
- **AND** the runner sends a cleanup prompt to the same agent session
- **AND** the agent commits or reverts changes so that `git status --porcelain` becomes empty
- **THEN** the task SHALL be marked completed
- **AND** the runner SHALL report the completed result with a cleanup attempt count

#### Scenario: Cleanup succeeds after multiple attempts

- **WHEN** an agent-backed task returns with uncommitted changes
- **AND** the first cleanup prompt does not fully resolve the dirty worktree
- **AND** a subsequent cleanup prompt within the bounded limit resolves all remaining changes
- **THEN** the task SHALL be marked completed
- **AND** the result SHALL record the total number of cleanup attempts

#### Scenario: Cleanup attempts exhausted

- **WHEN** an agent-backed task returns with uncommitted changes
- **AND** the runner exhausts the configured maximum cleanup attempts
- **AND** `git status --porcelain` still returns non-empty output
- **THEN** the task SHALL fail
- **AND** the failure SHALL include structured dirty-worktree evidence

### Requirement: Cleanup attempts have a default bound that is configurable

The cleanup loop SHALL have a default maximum of **3** attempts. Operators MAY lower or raise the bound by setting `runner.cleanup.maxAttempts` in the task's workflow variables. A value of `0` disables the cleanup loop entirely (deterministic failure on first dirty worktree, even for agent-backed tasks).

#### Scenario: Cleanup bound defaults to three attempts

- **WHEN** the runner enters the cleanup loop for an agent-backed task
- **AND** the workflow does not set `runner.cleanup.maxAttempts`
- **THEN** the runner SHALL attempt up to 3 cleanup follow-ups before failing the task

#### Scenario: Cleanup bound is overridable

- **WHEN** the workflow sets `runner.cleanup.maxAttempts` to a non-negative integer
- **THEN** the runner SHALL attempt up to that many cleanup follow-ups before failing the task

#### Scenario: Cleanup loop uses same agent session

- **WHEN** the runner sends a cleanup follow-up prompt
- **THEN** the prompt SHALL be sent to the same agent session used for the original task
- **AND** the runner SHALL NOT create a new agent session for cleanup
- **AND** the cleanup SHALL NOT be delegated to a different task or workflow stage

### Requirement: Dirty-worktree failure evidence is structured

When a task fails because the worktree remained dirty after the configured cleanup attempts, the failure output SHALL include structured evidence listing the files that prevented completion.

#### Scenario: Failure output includes categorized file lists

- **WHEN** a task fails with dirty-worktree evidence
- **THEN** the failure output SHALL include a list of staged files from `git diff --cached --name-only`
- **AND** it SHALL include a list of unstaged modified files from `git diff --name-only`
- **AND** it SHALL include a list of untracked files from `git ls-files --others --exclude-standard`

#### Scenario: Structured evidence is machine-readable

- **WHEN** dirty-worktree evidence is produced
- **THEN** the output SHALL be a JSON object with keys `staged`, `unstaged`, `untracked`, and `cleanupAttempts`
- **AND** each file list SHALL be an array of relative file paths
- **AND** the evidence SHALL include the number of cleanup attempts that were tried
