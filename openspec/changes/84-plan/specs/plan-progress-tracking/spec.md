## ADDED Requirements

### Requirement: Plan round complete SSE event
When `runPlanStage()` completes an artifact round (proposal, specs, design, tasks, self-review, auto-fix, re-self-review), the system SHALL emit a `plan_round_complete` event via EventBus with `{ issueId, projectId, roundType, roundLabel, roundIndex, duration, verdict? }`. The `duration` field SHALL contain the elapsed time in seconds since the round started. The `verdict` field SHALL be present only for self-review and re-self-review rounds, with value `'PASS'` or `'FAIL'`.

#### Scenario: Proposal round completes
- **WHEN** `runPlanStage()` finishes the proposal round successfully
- **THEN** EventBus emits `plan_round_complete` with `roundType: 'proposal'`, `roundIndex: 0`, `duration: <seconds>`
- **AND** `verdict` is absent

#### Scenario: Self-review round with PASS verdict
- **WHEN** the self-review round completes with PASS
- **THEN** EventBus emits `plan_round_complete` with `roundType: 'self-review'`, `roundIndex: 4`, `verdict: 'PASS'`

#### Scenario: Self-review round with FAIL verdict triggers auto-fix
- **WHEN** the self-review round completes with FAIL
- **THEN** EventBus emits `plan_round_complete` with `roundType: 'self-review'`, `roundIndex: 4`, `verdict: 'FAIL'`
- **AND** subsequent `plan_round_start` and `plan_round_complete` events are emitted for `auto-fix` (roundIndex: 5) and `re-self-review` (roundIndex: 6)

#### Scenario: Auto-fix and re-self-review complete cycle
- **WHEN** auto-fix completes followed by re-self-review with PASS
- **THEN** EventBus emits `plan_round_complete` for `auto-fix` (roundIndex: 5) and `re-self-review` (roundIndex: 6, verdict: 'PASS')

### Requirement: Plan stage calls emitProgress after each round
`runPlanStage()` SHALL call `this.emitProgress()` after each artifact round completes, passing `{ stage: 'plan', roundType, roundIndex, taskProgress: { completed: completedCount, total: totalSteps } }`. The `totalSteps` SHALL be 5 (proposal, specs, design, tasks, self-review). The `completedCount` SHALL increment after each round including auto-fix and re-self-review if they occur.

#### Scenario: After specs round completes
- **WHEN** the specs round (roundIndex 1) completes
- **THEN** `emitProgress` is called with `{ stage: 'plan', roundType: 'specs', roundIndex: 1, taskProgress: { completed: 2, total: 5 } }`

#### Scenario: After self-review PASS
- **WHEN** the self-review round completes with PASS
- **THEN** `emitProgress` is called with `{ stage: 'plan', roundType: 'self-review', roundIndex: 4, taskProgress: { completed: 5, total: 5 } }`

#### Scenario: After re-self-review PASS (auto-fix cycle)
- **WHEN** auto-fix (roundIndex 5) and re-self-review PASS (roundIndex 6) both complete
- **THEN** `emitProgress` is called for each, with `taskProgress.total: 5` unchanged and `completed` reflecting the additional rounds

### Requirement: Plan progress available via agent status API
The `/api/agent/status` endpoint SHALL include plan progress data in the `AgentProgress` response when the plan stage is active. The `taskProgress` field SHALL be populated with `{ completed, total }` reflecting completed and total plan steps. The `roundType` and `roundIndex` fields SHALL reflect the current or most recently completed round.

#### Scenario: Plan stage in progress at design round
- **WHEN** the agent is running the plan stage and currently on the design round (roundIndex 2)
- **THEN** `GET /api/agent/status` returns `AgentProgress` with `stage: 'plan'`, `roundType: 'design'`, `roundIndex: 2`, `taskProgress: { completed: 2, total: 5 }`

#### Scenario: Plan stage completed
- **WHEN** the plan stage has finished all rounds
- **THEN** `GET /api/agent/status` returns `AgentProgress` with `stage: 'plan'`, `roundType: 'self-review'` (or last completed round), `taskProgress: { completed: 5, total: 5 }`

### Requirement: Plan progress restored from checkpoint on resume
When `runPlanStage()` resumes from a checkpoint (server restart, issue reopen), it SHALL call `emitProgress()` once at the start with `taskProgress` reflecting previously completed steps, so the frontend can immediately display correct progress without waiting for new rounds.

#### Scenario: Resume with 3 completed steps
- **WHEN** `runPlanStage()` loads a checkpoint with completedSteps `['proposal', 'specs', 'design']`
- **THEN** `emitProgress` is called with `{ stage: 'plan', taskProgress: { completed: 3, total: 5 }, roundType: 'design', roundIndex: 2 }`

#### Scenario: Resume with all 5 steps completed
- **WHEN** `runPlanStage()` loads a checkpoint with all 5 steps completed
- **THEN** `emitProgress` is called with `{ stage: 'plan', taskProgress: { completed: 5, total: 5 } }`

### Requirement: PlanProgressPanel component displays step list
The frontend SHALL include a `PlanProgressPanel` component that renders a list of plan steps with status icons and labels. The step list SHALL be: proposal → specs → design → tasks → self-review. Each step SHALL display one of four statuses: pending (○), running (●), completed (✓), or failed (✗).

#### Scenario: Plan stage at design round
- **WHEN** the plan stage is active with 2 completed steps (proposal, specs) and currently on design
- **THEN** PlanProgressPanel renders: ✓ proposal, ✓ specs, ● design, ○ tasks, ○ self-review

#### Scenario: Self-review failed
- **WHEN** self-review completes with FAIL verdict
- **THEN** the self-review step shows ✗ status with "FAIL" label
- **AND** auto-fix and re-self-review steps appear appended to the list

### Requirement: PlanProgressPanel shows progress counter
The PlanProgressPanel SHALL display a header with a progress counter in the format `Plan Progress  X / 5 completed`, where X is the number of completed steps and 5 is the total base steps.

#### Scenario: 3 of 5 steps completed
- **WHEN** 3 steps have completed (proposal, specs, design)
- **THEN** the panel header shows "Plan Progress  3 / 5 completed"

#### Scenario: Auto-fix cycle active
- **WHEN** the base 5 steps are complete but auto-fix cycle is running
- **THEN** the counter shows "Plan Progress  5 / 5 completed" and auto-fix/re-self-review steps are shown as additional entries

### Requirement: Plan progress step duration display
Each completed step in the PlanProgressPanel SHALL display its elapsed duration in human-readable format (e.g., `2m 14s`). Durations under 1 minute SHALL display as seconds only (e.g., `45s`). Durations over 1 hour SHALL display hours and minutes (e.g., `1h 23m`).

#### Scenario: Step took 2 minutes 14 seconds
- **WHEN** a step completed with duration 134 seconds
- **THEN** the step shows "2m 14s" next to the status icon

#### Scenario: Step took 45 seconds
- **WHEN** a step completed with duration 45 seconds
- **THEN** the step shows "45s" next to the status icon

### Requirement: Self-review verdict displayed in step list
When the self-review or re-self-review step completes, its verdict (PASS or FAIL) SHALL be displayed inline in the step list, not only in the Approval Gate panel.

#### Scenario: Self-review PASS
- **WHEN** self-review completes with PASS verdict
- **THEN** the step shows "✓ self-review — PASS"

#### Scenario: Self-review FAIL then re-self-review PASS
- **WHEN** self-review fails (✗ FAIL) and re-self-review passes (✓ PASS)
- **THEN** the step list shows both verdicts: "✗ self-review — FAIL" and "✓ re-self-review — PASS"

### Requirement: Auto-fix cycle steps appended to step list
When self-review FAIL triggers an auto-fix cycle, the PlanProgressPanel SHALL append two additional steps: `auto-fix` and `re-self-review`. These steps SHALL follow the same status and duration display rules as the base 5 steps.

#### Scenario: Auto-fix cycle in progress
- **WHEN** self-review FAIL has occurred and auto-fix is currently running
- **THEN** the step list shows: ... ✓ tasks, ✗ self-review — FAIL, ● auto-fix, ○ re-self-review

#### Scenario: Auto-fix cycle completed with PASS
- **WHEN** auto-fix and re-self-review both complete with PASS
- **THEN** the step list shows: ... ✓ tasks, ✗ self-review — FAIL, ✓ auto-fix, ✓ re-self-review — PASS

### Requirement: Frontend consumes plan_round_complete events
The `useSessionTimeline` hook SHALL subscribe to `plan_round_complete` SSE events and maintain a `planProgress` state object. The `planProgress` state SHALL include: `steps` (array of `{ roundType, roundLabel, roundIndex, status, duration?, verdict? }`), `completedCount`, `totalSteps`. When a `plan_round_complete` event is received, the corresponding step's status SHALL be updated to `completed` (or `failed` if verdict is `FAIL`), with the `duration` and `verdict` stored.

#### Scenario: plan_round_complete event received
- **WHEN** a `plan_round_complete` SSE event arrives with `roundType: 'design'`, `roundIndex: 2`, `duration: 95`
- **THEN** the design step in `planProgress.steps` is updated to `{ status: 'completed', duration: 95 }`
- **AND** `planProgress.completedCount` is incremented

#### Scenario: plan_round_complete with FAIL verdict
- **WHEN** a `plan_round_complete` event arrives with `roundType: 'self-review'`, `verdict: 'FAIL'`
- **THEN** the self-review step is updated to `{ status: 'failed', verdict: 'FAIL' }`
- **AND** auto-fix and re-self-review steps are appended to the steps array with status `pending`

### Requirement: Frontend initializes plan progress from agent status
When the page loads and an agent is running on the plan stage, the frontend SHALL initialize `planProgress` from the `AgentProgress.taskProgress` data returned by `/api/agent/status`. Steps corresponding to `completedCount` SHALL be marked as completed, the current step as running, and remaining steps as pending.

#### Scenario: Page loads mid-plan
- **WHEN** the user opens the issue detail page while the plan stage is running at roundIndex 2 (design), with `taskProgress: { completed: 2, total: 5 }`
- **THEN** `planProgress` is initialized with: proposal (completed), specs (completed), design (running), tasks (pending), self-review (pending)

#### Scenario: Page loads after plan completion
- **WHEN** the user opens the issue detail page after plan stage completed all 5 rounds
- **THEN** `planProgress` is initialized with all 5 steps marked as completed, with no running step

### Requirement: PlanProgressPanel rendered in plan stage
The `SessionTimeline` component SHALL render `PlanProgressPanel` when `currentStage === 'plan'` and there are plan progress steps to display. The panel SHALL be positioned above the round-based conversation timeline, matching the visual pattern of `TaskProgressPanel` in the build stage.

#### Scenario: Plan stage active with progress data
- **WHEN** `currentStage` is `'plan'` and `planProgress.steps` has entries
- **THEN** `PlanProgressPanel` is rendered above the round timeline

#### Scenario: Build stage active
- **WHEN** `currentStage` is `'build'`
- **THEN** `PlanProgressPanel` is NOT rendered (existing `TaskProgressPanel` is rendered instead)
