### Requirement: Agent tasks are identified by origin.uses, sessionName, and classification — never by workType

The Web SHALL identify an agent task (a task backed by an AI agent session) using only data it already trusts at the task level: `origin.uses === 'mohist/acp-agent'`, the presence of a non-empty `sessionName`, and the task's `classification`. workType SHALL NOT be used as the deciding field, because workType is not a task-level field. Pure ops tasks (e.g. `mohist/rebase`, `core/process`) SHALL be treated as non-agent and SHALL receive no milestone rows.

#### Scenario: A task whose origin.uses is the agent action is recognized as an agent task

- **WHEN** a task carries `origin.uses === 'mohist/acp-agent'` (and a `sessionName`)
- **THEN** the panel SHALL treat it as an agent task and SHALL be eligible to render milestone rows

#### Scenario: A pure ops task is not treated as an agent task

- **WHEN** a task carries `origin.uses` of `mohist/rebase`, `core/process`, or another ops action (and no `sessionName`)
- **THEN** the panel SHALL NOT render any milestone rows for that task

#### Scenario: workType is not consulted to decide milestone eligibility

- **WHEN** the milestone eligibility decision is made
- **THEN** no workType field SHALL be read, because workType is not present at the task level

### Requirement: A task's agent session is resolved by joining sessionName to the existing workflow-run sessions data

The panel SHALL resolve an agent task's backing session by joining the task's `sessionName` to the session of the same `sessionName` within the existing workflow-run sessions data (`useWorkflowRunSessions(workflowRunId)`). No new endpoint, no new query parameter, and no new wire type SHALL be introduced to resolve the session. When no session matches the `sessionName`, the panel SHALL degrade to showing no milestone rows for that task (rather than erroring).

#### Scenario: sessionName join resolves the backing session

- **WHEN** an agent task has `sessionName === 'plan-issue-339'` and the workflow-run sessions data contains a session with `sessionName === 'plan-issue-339'`
- **THEN** that session's summary SHALL be the source of the task's milestone facts

#### Scenario: A missing session match degrades gracefully

- **WHEN** an agent task's `sessionName` matches no session in the workflow-run sessions data
- **THEN** the panel SHALL render no milestone rows for that task
- **AND** SHALL NOT raise an error or block the ops log from rendering

#### Scenario: No new network surface is introduced to resolve the session

- **WHEN** the panel resolves a task's session
- **THEN** no new server endpoint or query parameter SHALL be exercised beyond the existing workflow-run sessions query already used elsewhere in the Web

### Requirement: The milestone set is derived from the existing session summary fields

The panel SHALL derive the milestone set exclusively from fields already present on the resolved `WorkflowRunSession` summary: the bound/resolved model (from `eventSummary.resolvedModel`, falling back to the session's `model`), the session end `status`, the `failureReason`, and the relevant timestamps (`createdAt`/`startedAt` for the session-begin anchor and `completedAt` for the session-end anchor). No new field SHALL be added to the session summary to support milestones. The milestones SHALL be boundary/summary facts only — the agent's turn-by-turn dialogue SHALL NOT be derived as a milestone (that remains the transcript's responsibility).

#### Scenario: The bound model milestone is read from the resolved model

- **WHEN** the resolved session's `eventSummary.resolvedModel` is set (or, failing that, its `model`)
- **THEN** a milestone representing the bound/resolved model SHALL be derivable from that value

#### Scenario: The session-end milestone carries status and failure reason

- **WHEN** the resolved session has reached an end `status` (e.g. completed or failed)
- **THEN** a session-end milestone SHALL be derivable carrying that status
- **AND** when the status is a failure, the milestone SHALL also carry the session's `failureReason`

#### Scenario: Agent dialogue is never surfaced as a milestone

- **WHEN** the milestone set is derived
- **THEN** no milestone SHALL reproduce the agent's turn-by-turn dialogue content
- **AND** users looking for dialogue detail SHALL be directed to the transcript

### Requirement: Milestones are a transient view-layer projection that never enters the task-log store or log cache

Milestone rows SHALL be computed transiently at render time from the session summary and SHALL NOT be persisted into the task-log store, SHALL NOT be merged into the `TaskLogPage` React Query cache, and SHALL NOT flow through `mergeTaskLogDelta`. The runner collection pipeline, the server, the task-log REST endpoint, and the SignalR delta channel SHALL receive no change to emit milestone rows. The task-log store and the transcript store SHALL remain independent domain stores after this change — coupled only at render time, never in the domain.

#### Scenario: Milestones are not written to the task-log store

- **WHEN** an agent task's milestone rows are rendered
- **THEN** no milestone row SHALL be persisted into the task-log store on either side of the wire

#### Scenario: Milestones bypass the log cache and delta merge

- **WHEN** a task-log delta arrives or the `TaskLogPage` cache is read
- **THEN** milestone rows SHALL NOT appear in the cached `lines` and SHALL NOT be produced by `mergeTaskLogDelta`
- **AND** the `{ seq, timestamp, source, text }` `TaskLogLine` model SHALL remain unchanged

#### Scenario: No runner or server change is required to emit milestones

- **WHEN** the milestone feature is delivered
- **THEN** the runner SHALL NOT collect session milestones into the task-log pipeline
- **AND** the server task-log endpoint and SignalR delta channel SHALL remain as in Phase 1/2

### Requirement: Terminal-state milestone facts are obtainable from the persisted summary without the real-time channel

The acceptance floor for milestones SHALL be terminal-state visibility: once the session has ended, the bound-model milestone and the session-end milestone (status and, on failure, the reason) SHALL be renderable from the persisted session summary alone. This SHALL NOT depend on the Phase 2 real-time channel being live. Live display of the bound model while the session is still mid-flight is an enhancement that rides the existing sessions live-patch; it is not a hard acceptance item.

#### Scenario: A finished session's milestones render without any live channel

- **WHEN** an agent task's session has ended and the task panel is opened after the fact
- **THEN** the bound-model and session-end (status, and on failure the reason) milestones SHALL be visible
- **AND** SHALL NOT require a real-time session event to have been observed

#### Scenario: Mid-session live model display is optional

- **WHEN** an agent task's session is still running
- **THEN** the bound-model milestone MAY appear as soon as the resolved model is known (via the existing sessions live-patch)
- **BUT** its absence mid-session SHALL NOT count as a failure of the acceptance floor

### Requirement: TaskProgressPanel preserves sessionName, origin, and classification and forwards sessionName into the task log panel

The `TaskProgressPanel` timeline-to-`StageTaskState` mapping SHALL preserve `sessionName`, `origin` (including `uses`), and `classification` — mirroring `WorkflowView`'s `workflowTimelineToStageStateMap`, which today retains these fields where `TaskProgressPanel` drops them. The `sessionName` SHALL be forwarded into the `TaskLogPanel` so the panel can join it against the workflow-run sessions data. This is the data-flow precondition that makes the join in the requirements above possible.

#### Scenario: sessionName, origin.uses, and classification survive the TaskProgressPanel mapping

- **WHEN** the workflow timeline carries a task with `sessionName`, `uses`, and `classification`
- **THEN** the `StageTaskState` produced by `TaskProgressPanel` SHALL retain those three fields
- **AND** SHALL forward `sessionName` into the rendered `TaskLogPanel`

#### Scenario: TaskProgressPanel field retention mirrors WorkflowView

- **WHEN** the same workflow timeline is rendered by both `WorkflowView` and `TaskProgressPanel`
- **THEN** the `sessionName`, `origin`, and `classification` values for a given task SHALL be identical between the two mappings
