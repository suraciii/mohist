## MODIFIED Requirements

### Requirement: Task failure handling with retry
The system SHALL handle task failures with categorized retry logic.

**Failure Categories:**

| Type | Examples | Retry | Max Attempts |
|------|----------|-------|--------------|
| AC not met | Missing validation | Yes | 3 total |
| Environment | npm install failed | Yes | 2 total |
| Dependency | Can't find module | No | - |
| Timeout (no WIP) | >30min execution, no changes saved | No | - |
| Timeout (with WIP) | >30min execution, WIP commit saved | Yes | 2 total |

#### Scenario: Handle AC failure with retry
- **WHEN** task T-003 fails because AC "backend validation" not met
- **THEN** main-agent:
  1. Extracts failure reason: "Only frontend validation implemented"
  2. Stores learning with failure context
  3. If attempts < 3:
     - Assembles retry prompt with failure context
     - Calls spawn_coder again
  4. If attempts >= 3:
     - Pauses build
     - Asks user: retry, skip, or abort

#### Scenario: Handle non-retryable failure
- **WHEN** task fails due to "Cannot find auth module export"
- **AND** it's a dependency/code issue (not retryable)
- **THEN** main-agent immediately pauses
- **AND** asks user for guidance
- **AND** stores the dependency issue in learning

#### Scenario: Handle timeout with WIP commit
- **WHEN** task T-001 times out with WIP commit saved
- **THEN** main-agent:
  1. Queries the WIP commit for T-001 (changed files, diff summary)
  2. If attempts < 2:
     - Assembles retry prompt including WIP context (see Requirement: Task execution context assembly)
     - Calls spawn_coder with resume prompt
  3. If attempts >= 2:
     - Pauses build
     - Asks user: retry (from WIP), skip, or abort

#### Scenario: Handle timeout without WIP commit
- **WHEN** task T-001 times out with no WIP commit (no changes were made)
- **THEN** main-agent immediately pauses
- **AND** asks user for guidance
- **AND** records the failure as non-retryable timeout

### Requirement: Task execution context assembly
The system SHALL assemble complete context for each task execution.

**Context Components:**
1. System prompt defining the agent role
2. proposal.md for background
3. design.md for technical constraints
4. The specific spec file referenced by task.spec
5. Session memories from previous tasks (insights + adjustments)
6. Task description and acceptanceCriteria
7. WIP resume context (when retrying a timed-out task with WIP commit)

#### Scenario: Build task context
- **WHEN** executing task T-003
- **THEN** the main-agent assembles:
  ```
  [System] You are the Mohist Coder Agent...
  
  [Proposal] {proposal.md content}
  
  [Design] {design.md content}
  
  [Current Requirement] {specs/auth/spec.md content}
  
  [Previous Learnings]
  From T-001: "Project uses single quotes"
  From T-002: "Tests need docker"
  
  [Task T-003]
  Description: Implement login API
  AC:
  - POST /api/login returns JWT
  - Validates email format
  - Returns 401 for invalid credentials
  ```

#### Scenario: Build retry context with WIP resume
- **WHEN** retrying task T-001 after timeout with a WIP commit
- **THEN** the main-agent assembles the standard context plus:
  ```
  [WIP Resume]
  Task T-001 timed out on attempt 1.
  A WIP commit was saved with the following progress:
  
  Modified files:
  - packages/cli/src/types/index.ts
  - packages/cli/src/services/merge-queue.ts
  - packages/cli/src/git/worktree-manager.ts
  
  Diff summary:
  {git diff --stat of WIP commit}
  
  Continue from this state. Do NOT re-read or re-implement the files listed above.
  Focus on completing the remaining acceptance criteria.
  ```
- **AND** the coder agent SHALL be spawned in the worktree that already has the WIP commit on HEAD
