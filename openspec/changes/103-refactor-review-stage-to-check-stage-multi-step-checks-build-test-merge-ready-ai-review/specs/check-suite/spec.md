## ADDED Requirements

### Requirement: Check Suite sequential execution

The Check stage SHALL execute a sequential suite of checks: Build & Test → Merge Ready (optional) → AI Code Review. Each check runs only after the previous check passes. A check can be skipped based on workflow.yaml configuration.

#### Scenario: All checks pass sequentially
- **WHEN** Check stage starts
- **THEN** Build & Test check runs first
- **AND** when Build & Test passes, Merge Ready check runs
- **AND** when Merge Ready completes, AI Code Review check runs
- **AND** when AI Code Review passes, stage transitions to approval gate

#### Scenario: Build & Test fails stops the suite
- **WHEN** Build & Test check fails (including after auto-fix exhaustion)
- **THEN** Merge Ready check SHALL NOT run
- **AND** AI Code Review check SHALL NOT run
- **AND** stage pauses with check results showing Build & Test failure

#### Scenario: Disabled checks are skipped
- **WHEN** workflow.yaml configures `ff-merge.enabled: false`
- **THEN** Merge Ready check is skipped entirely
- **AND** execution proceeds from Build & Test directly to AI Code Review
- **WHEN** workflow.yaml configures `ai-review.enabled: false`
- **THEN** AI Code Review check is skipped
- **AND** execution proceeds from Merge Ready (or Build & Test) directly to approval gate

### Requirement: CheckResult data model

Each check SHALL produce a `CheckResult` object with the following fields: `{ name: string, status: 'pending' | 'running' | 'passed' | 'failed', duration?: number, autoFixed?: boolean, summary?: string, verdict?: string, dimensions?: string[], reviewReport?: string, buildLog?: string, conflictFiles?: string[] }`.

#### Scenario: CheckResult for Build & Test pass
- **WHEN** Build & Test check passes on first attempt
- **THEN** `CheckResult` has `name: 'build-test'`, `status: 'passed'`, `autoFixed: false`, `duration` in milliseconds, `summary` containing build/test output summary

#### Scenario: CheckResult for Build & Test auto-fixed
- **WHEN** Build & Test check fails initially but auto-fix succeeds
- **THEN** `CheckResult` has `name: 'build-test'`, `status: 'passed'`, `autoFixed: true`, `summary` describing the fix

#### Scenario: CheckResult for AI Code Review pass
- **WHEN** AI Code Review check passes
- **THEN** `CheckResult` has `name: 'ai-review'`, `status: 'passed'`, `verdict: 'PASS'`, `reviewReport` containing the full review markdown

#### Scenario: CheckResult for AI Code Review fail
- **WHEN** AI Code Review check fails (including after auto-fix exhaustion)
- **THEN** `CheckResult` has `name: 'ai-review'`, `status: 'failed'`, `verdict: 'FAIL'`, `reviewReport` containing the full review markdown with fix suggestions

### Requirement: CheckSuiteOutput storage

The Check stage SHALL produce a `CheckSuiteOutput { checks: CheckResult[], overallResult: 'passed' | 'failed' | 'blocked' }` and store it in `approvalState.output`.

#### Scenario: All checks pass
- **WHEN** all enabled checks pass
- **THEN** `CheckSuiteOutput.overallResult` is `'passed'`

#### Scenario: Any blocking check fails
- **WHEN** Build & Test or AI Code Review fails
- **THEN** `CheckSuiteOutput.overallResult` is `'failed'`

### Requirement: Build & Test check

The Build & Test check SHALL run configurable build and test commands in the issue's worktree. When the check fails, it SHALL attempt automatic fix via coder agent (max 2 attempts). The command, timeout, and autoFix behavior SHALL be configurable via workflow.yaml.

#### Scenario: Build & Test passes on first attempt
- **WHEN** `npm run build && npm test` (or configured command) exits with code 0
- **THEN** check status is `'passed'`
- **AND** execution proceeds to the next check

#### Scenario: Build & Test fails, auto-fix succeeds
- **WHEN** configured command exits with non-zero code
- **AND** auto-fix is enabled (`checks.build-test.autoFix: true`)
- **THEN** system spawns a coder agent to fix the errors (attempt 1)
- **AND** re-runs the build/test command
- **AND** if it passes, check status is `'passed'` with `autoFixed: true`
- **AND** if it fails again, system spawns coder agent again (attempt 2)
- **AND** if it passes on attempt 2, check status is `'passed'` with `autoFixed: true`

#### Scenario: Build & Test fails, auto-fix exhausted
- **WHEN** configured command exits with non-zero code
- **AND** auto-fix has been attempted 2 times without success
- **THEN** check status is `'failed'`
- **AND** `buildLog` contains the error output
- **AND** stage pauses with only [退回去重做] action available

#### Scenario: Build & Test auto-fix disabled
- **WHEN** `checks.build-test.autoFix: false`
- **AND** configured command exits with non-zero code
- **THEN** check status is `'failed'` immediately without auto-fix attempts
- **AND** stage pauses with only [退回去重做] action available

#### Scenario: Build & Test timeout
- **WHEN** build/test command exceeds the configured timeout (default 5 minutes)
- **THEN** check status is `'failed'`
- **AND** `buildLog` indicates timeout
- **AND** stage pauses with only [退回去重做] action available

### Requirement: Merge Ready check

The Merge Ready check SHALL perform a dry-run `git merge-base --is-ancestor` test to determine if the branch can fast-forward merge into the base branch. This check is informational and SHALL NOT block the pipeline.

#### Scenario: Branch is fast-forwardable
- **WHEN** the issue branch HEAD is an ancestor of the base branch HEAD
- **THEN** `CheckResult` has `name: 'merge-ready'`, `status: 'passed'`, `summary: 'Merge Ready: yes'`

#### Scenario: Branch needs rebase
- **WHEN** the issue branch HEAD is NOT an ancestor of the base branch HEAD
- **THEN** `CheckResult` has `name: 'merge-ready'`, `status: 'passed'`, `summary: 'Merge Ready: needs rebase'`
- **AND** the check SHALL NOT block pipeline progression
- **AND** actual rebase is deferred to MergeQueue post-approval

#### Scenario: Merge Ready check disabled
- **WHEN** `checks.ff-merge.enabled: false`
- **THEN** Merge Ready check is skipped entirely
- **AND** no CheckResult entry is produced for merge-ready

### Requirement: AI Code Review check

The AI Code Review check SHALL preserve the existing review behavior: run reviewer agent → self-check → parse verdict → auto-fix loop (max 2). The output SHALL be stored as a CheckResult within the CheckSuiteOutput.

#### Scenario: AI Review passes on first attempt
- **WHEN** reviewer agent runs and self-check produces `Result: PASS`
- **THEN** `CheckResult` has `status: 'passed'`, `verdict: 'PASS'`, `reviewReport` with the full review
- **AND** stage transitions to approval gate

#### Scenario: AI Review fails, auto-fix succeeds
- **WHEN** self-check produces `Result: FAIL` with fix suggestions
- **AND** auto-fix loop (max 2 attempts) produces `Result: PASS`
- **THEN** `CheckResult` has `status: 'passed'`, `autoFixed: true`, `reviewReport` with the updated review
- **AND** stage transitions to approval gate

#### Scenario: AI Review fails, auto-fix exhausted
- **WHEN** self-check produces `Result: FAIL`
- **AND** auto-fix loop exhausts 2 attempts without producing PASS
- **THEN** `CheckResult` has `status: 'failed'`, `verdict: 'FAIL'`, `reviewReport` with the latest review
- **AND** stage pauses with actions [退回去修] [添加指令] [强行批准]

#### Scenario: AI Review fails with no fix suggestions
- **WHEN** self-check produces `Result: FAIL` but no fix suggestions are extractable
- **THEN** `CheckResult` has `status: 'failed'`, `verdict: 'FAIL'`, `reviewReport` with the review
- **AND** stage pauses with actions [退回去修] [添加指令] [强行批准]

### Requirement: Approval actions gated by check results

The approval gate SHALL present different actions based on `CheckSuiteOutput.overallResult` and individual check statuses.

#### Scenario: All checks pass — approve and merge
- **WHEN** `overallResult` is `'passed'`
- **THEN** user can approve, which triggers `MergeQueue.enqueue()`
- **AND** issue stage remains `check`, `mergeState` becomes `pending`

#### Scenario: Build & Test failed — only rollback
- **WHEN** Build & Test `CheckResult.status` is `'failed'`
- **THEN** only [退回去重做] (back to Build stage) action is available
- **AND** approve action is NOT available

#### Scenario: AI Review failed — limited approval
- **WHEN** AI Code Review `CheckResult.status` is `'failed'`
- **THEN** available actions are [退回去修] (back to Build), [添加指令] (inject message and retry), [强行批准] (force approve despite issues)

### Requirement: Post-approval MergeQueue integration

After user approves, the system SHALL call `MergeQueue.enqueue()` instead of invoking `mergeBackFn` directly. All merges go through MergeQueue's serial processing.

#### Scenario: User approves after all checks pass
- **WHEN** user approves an issue in Check stage with `overallResult: 'passed'`
- **THEN** system calls `MergeQueue.enqueue(projectId, issueNumber)`
- **AND** issue `mergeState` is set to `pending`
- **AND** issue stage remains `check` until MergeQueue completes

#### Scenario: MergeQueue merge succeeds
- **WHEN** MergeQueue successfully merges the issue
- **THEN** issue stage transitions to `done`
- **AND** issue `mergeState` is set to `merged`

#### Scenario: MergeQueue merge fails
- **WHEN** MergeQueue fails (conflict, build failure, etc.)
- **THEN** issue `mergeState` is set to the failure state (`blocked`, `conflict`, or `build-failed`)
- **AND** issue stage remains `check`
- **AND** user can retry via existing MergeQueue retry mechanism
