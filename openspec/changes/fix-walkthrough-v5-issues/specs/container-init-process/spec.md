## ADDED Requirements

### Requirement: Coder agent commits changes after each task

The task prompt sent to the coder agent via ACP SHALL include an instruction to commit code changes after completing the task. This ensures that files created by one task are visible to subsequent tasks and that partial progress is preserved if the build fails.

#### Scenario: Agent receives git commit instruction in prompt
- **WHEN** `buildTaskContext()` in `context-assembler.ts` assembles the full prompt for a task
- **THEN** the resulting `fullPrompt` ends with a section instructing the agent to run `git add -A && git commit -m "<task-id>: <brief description>"` after completing the task

#### Scenario: Task artifacts are committed between tasks
- **WHEN** the coder agent completes task T-001 and the ralph executor starts task T-002
- **THEN** `git log` in the worktree shows a commit from T-001, and files from T-001 are tracked

#### Scenario: Build failure preserves completed task code
- **WHEN** a build stage fails after completing some tasks
- **THEN** completed task code is preserved in git history (not lost as untracked files)

### Requirement: Walkthrough container uses entrypoint.sh

The mohist-walkthrough SKILL.md SHALL use entrypoint.sh (the container's default ENTRYPOINT) instead of `sleep infinity` as the container command. This enables SIGTERM handling and zombie process reclamation.

#### Scenario: Container responds to SIGTERM
- **WHEN** `podman stop` sends SIGTERM to the container
- **THEN** the container exits gracefully within 10 seconds without requiring SIGKILL

#### Scenario: No zombie processes accumulate
- **WHEN** multiple agent processes are spawned and exit during a walkthrough session
- **THEN** `ps aux` shows no `[sh] defunct` zombie processes
