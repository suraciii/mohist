## MODIFIED Requirements

### Requirement: REQ-WA-001 Workflow consumes session results without judging liveness

Workflow orchestration SHALL consume completed, failed, or cancelled session call results from tasks and SHALL NOT independently determine whether opencode is alive. After an agent-backed task returns a session result, the runner SHALL verify `git status --porcelain` is clean in the task workspace before marking the task completed. If the worktree is dirty, the runner SHALL enter the cleanup loop before consuming the session result as complete.

#### Scenario: Workflow receives session failure

- **WHEN** a task reports that its opencode session failed
- **THEN** workflow SHALL handle that as a task/session execution result
- **AND** workflow SHALL decide retry, block, interruption, or user action through existing workflow policy

#### Scenario: Session state does not mutate issue state directly

- **WHEN** a session enters `probing` or `failed`
- **THEN** issue `stage` and `status` SHALL remain unchanged unless a separate workflow decision changes them later

#### Scenario: Agent session result with dirty worktree triggers cleanup

- **WHEN** an agent-backed task returns a successful session result
- **AND** `git status --porcelain` is non-empty in the task workspace
- **THEN** the runner SHALL NOT report the task as completed
- **AND** the runner SHALL send a cleanup follow-up prompt to the same agent session
- **AND** the task SHALL NOT be consumed as complete by workflow orchestration until the cleanup loop resolves the dirty worktree or exhausts its attempts

## ADDED Requirements

### Requirement: Agent cleanup prompt constrains scope and outputs

The cleanup follow-up prompt sent to the agent SHALL explicitly constrain the agent to only commit or revert changes from the current task. The prompt SHALL instruct the agent to report a summary and either a commit SHA or a no-change result.

#### Scenario: Cleanup prompt restricts agent actions

- **WHEN** the runner sends a cleanup follow-up prompt to an agent session
- **THEN** the prompt SHALL explicitly instruct the agent NOT to start new implementation work
- **AND** the prompt SHALL explicitly instruct the agent NOT to push to any remote
- **AND** the prompt SHALL explicitly instruct the agent NOT to modify code outside the scope of cleanup
- **AND** the prompt SHALL instruct the agent to inspect uncommitted changes and decide whether to commit or revert each change

#### Scenario: Agent reports commit or no-change result

- **WHEN** an agent completes a cleanup follow-up
- **THEN** the agent SHALL report either a commit SHA for the committed cleanup changes
- **OR** the agent SHALL report a no-change result indicating no commit was needed
- **AND** the runner SHALL verify the actual `git status --porcelain` output regardless of what the agent reported

### Requirement: Agent cleanup attempts are bounded

The cleanup loop for a single agent-backed task SHALL have a maximum number of attempts. After the limit is exhausted, the task SHALL fail rather than looping indefinitely.

#### Scenario: Cleanup attempts have a configurable maximum

- **WHEN** the runner enters the cleanup loop for a task
- **THEN** it SHALL only attempt up to the configured maximum number of cleanup follow-up prompts
- **AND** the default maximum SHALL be a small fixed number suitable for preventing infinite loops

#### Scenario: Exhausted cleanup fails the task with structured evidence

- **WHEN** the cleanup loop exhausts all attempts
- **AND** `git status --porcelain` still returns non-empty output
- **THEN** the task SHALL fail
- **AND** the failure evidence SHALL include the number of cleanup attempts tried
- **AND** it SHALL include the categorized file lists from `git status --porcelain`
