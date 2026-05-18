## MODIFIED Requirements

### Requirement: TaskRun references shared agent sessions without owning transcripts

WorkflowRun task execution SHALL preserve TaskRun as the user-visible work unit and SHALL allow an agent-backed TaskRun to reference at most one logical agent session reference. Multiple TaskRuns MAY reference the same logical session, but task completion, failure, attempts, duration, output, and artifact evidence SHALL remain task-owned state.

#### Scenario: Shared session preserves separate task results
- **WHEN** Plan executes `proposal`, `specs`, `design`, `tasks`, and `self-review` through the same `agentSessionRef`
- **THEN** the Plan StageRun SHALL record independent TaskRun results for each task
- **AND** WorkflowRun SHALL NOT infer task completion from the referenced session reaching a terminal state

#### Scenario: Session failure is task evidence
- **WHEN** execution against a named agent session fails for one task
- **THEN** the task attempt SHALL record failed task evidence
- **AND** WorkflowRun SHALL decide retry, failure, or approval behavior through normal task and stage policy rather than session status alone

### Requirement: Stage attempt boundaries create fresh named sessions

Named agent session references SHALL resolve within the active WorkflowRun StageRun attempt. Retry, rerun, or rewind of a stage SHALL create a fresh real session for the same logical ref instead of appending prompts to an old completed transcript.

#### Scenario: Same attempt reuses named session
- **WHEN** multiple non-restored tasks in the same stage attempt use `agentSessionRef: "plan-artifacts"`
- **THEN** they SHALL resolve to the same real agent session instance

#### Scenario: New attempt does not append to old transcript
- **WHEN** the Plan stage is retried, rerun, or rewound after a previous attempt used `agentSessionRef: "plan-artifacts"`
- **THEN** the new stage attempt SHALL resolve `plan-artifacts` to a new real agent session instance
- **AND** the old session SHALL remain historical evidence

#### Scenario: Restore and skip do not change later ownership
- **WHEN** an intermediate Plan artifact task is restored from disk or skipped
- **THEN** that restored or skipped task SHALL NOT create a session solely because its policy has `agentSessionRef`
- **AND** later non-restored tasks SHALL still resolve their configured ref deterministically
