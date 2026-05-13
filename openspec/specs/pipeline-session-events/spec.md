# OpenSpec Capability: pipeline-session-events

### Requirement: AcpConnectionOptions extended with issueNumber and onSessionUpdate

`AcpConnectionOptions` and `AcpSessionOptions` SHALL include two new optional fields:
- `issueNumber?: number` — used for SSE event `issueId` (frontend matches by issue number, not UUID)
- `onSessionUpdate?: (notification: SessionNotification) => void` — callback for external event processing (used by Plan/Review stage bridge)

When `onSessionUpdate` is provided, `createAcpConnection` SHALL call it for every sessionUpdate notification and SHALL NOT emit `coder_text_chunk` or `coder_tool_call` events internally. When not provided, behavior is unchanged.

#### Scenario: Plan stage uses onSessionUpdate
- **WHEN** `createAcpConnection` is called with `onSessionUpdate` set
- **THEN** for each ACP sessionUpdate: agentText accumulates normally, `workflowLogRepo.insert()` executes, `onSessionUpdate(notification)` is called
- **AND** `coder_text_chunk` and `coder_tool_call` are NOT emitted

#### Scenario: Build stage uses default behavior
- **WHEN** `runAcpSession` is called without `onSessionUpdate`
- **THEN** behavior is unchanged: `coder_text_chunk` and `coder_tool_call` are emitted as before

### Requirement: acpOptions includes eventBus + issueNumber in all pipeline routes

All 5 `acpOptions` constructions in `api/issues.ts` SHALL include the `eventBus` singleton and `issueNumber: issue.number`. The 5 sites are: `start`, `reopen`, `approve`, `reject`, `messages`.

#### Scenario: Start pipeline route passes eventBus and issueNumber
- **WHEN** `POST /api/issues/:number/start` constructs `acpOptions`
- **THEN** `acpOptions.eventBus` is set to the imported singleton AND `acpOptions.issueNumber` is set to `issue.number`

#### Scenario: Reopen route passes eventBus and issueNumber
- **WHEN** `POST /api/issues/:number/reopen` constructs `acpOptions` (when resuming pipeline)
- **THEN** `acpOptions.eventBus` and `acpOptions.issueNumber` are set

### Requirement: SSE event issueId uses issue number via dual-track

In `acp-session.ts`, SSE event emission SHALL use `String(options.issueNumber ?? options.issueId)` as the `issueId` field. DB operations (`workflowLogRepo.insert`, `coderSessionRepo.insert`) SHALL continue using `options.issueId` (UUID) unchanged.

#### Scenario: coder_text_chunk with issueNumber
- **WHEN** `issueNumber: 5` is passed in options
- **THEN** `coder_text_chunk` event has `issueId: "5"`

#### Scenario: Fallback when issueNumber not provided
- **WHEN** `issueNumber` is undefined (e.g., Explore sessions)
- **THEN** SSE event `issueId` falls back to `issueId` (UUID)

### Requirement: Plan/Review stage sets stage-specific executionId

`WorkflowController.run()` SHALL override `acpOptions.executionId` before dispatching to each stage:
- Plan: `'plan-${issue.number}'`
- Review: `'review-${issue.number}'`

Build stage executionId is set per-task by `RalphExecutor`.

#### Scenario: Plan stage executionId
- **WHEN** `runPlanStage` is called
- **THEN** the acpOptions passed to `createAcpConnection` has `executionId: 'plan-1'` (for issue #1)

### Requirement: Plan stage emits round start events

When `runPlanStage()` begins a new round (proposal / specs / design / tasks / self-review), the system SHALL emit a `plan_round_start` event via the `onSessionUpdate` bridge with `{ issueId, projectId, roundType, roundLabel, roundIndex }`.

#### Scenario: Proposal round starts
- **WHEN** `runPlanStage()` begins the first round with `type: 'proposal'`
- **THEN** EventBus emits `plan_round_start` with `roundType: 'proposal'`, `roundLabel: 'proposal.md'`, `roundIndex: 0`

#### Scenario: Self-review round starts
- **WHEN** `runPlanStage()` begins the self-review round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'self-review'`, `roundIndex: 4`

### Requirement: Plan/Review stage bridges sessionUpdate via onSessionUpdate

For each sessionUpdate received from the multi-round ACP connection in `runPlanStage()` and `runPipelineReviewStage()`, the `onSessionUpdate` callback SHALL emit a `plan_session_update` event to EventBus with `{ issueId, projectId, roundType, roundIndex, sessionUpdate, data }`. The `data` field SHALL contain the full sessionUpdate payload.

#### Scenario: Agent message chunk in specs round
- **WHEN** ACP connection reports an `agent_message_chunk` sessionUpdate during the specs round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'specs'`, `sessionUpdate: 'agent_message_chunk'`, and `data` containing the text content

#### Scenario: Tool call completed in design round
- **WHEN** ACP connection reports a `tool_call_update` with `status: 'completed'` during the design round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'design'`, `sessionUpdate: 'tool_call_update'`, and `data` containing rawInput, rawOutput, kind, title

#### Scenario: Review stage uses same mechanism
- **WHEN** `runPipelineReviewStage` receives a sessionUpdate
- **THEN** EventBus emits `plan_session_update` with `roundType: 'review'`

### Requirement: Plan session events registered in SSE event types

The `plan_round_start` and `plan_session_update` events SHALL be included in all SSE event type registrations:
- `events.ts` `ALL_EVENT_TYPES` array (backend)
- `agent-events.ts` `AGENT_DETAIL_EVENTS` array (frontend)
- `useSSE.ts` `eventTypes` array (frontend)

#### Scenario: SSE client receives plan round start
- **WHEN** a WebUI SSE client is connected and a plan round starts
- **THEN** the client receives `event: plan_round_start` with the round metadata

### Requirement: EventBridge logic is fire-and-forget

All EventBus emit calls in the plan/review stage bridge SHALL be fire-and-forget. Emit failures SHALL NOT affect the pipeline execution flow.

#### Scenario: EventBus emit throws during plan stage
- **WHEN** `onSessionUpdate` callback encounters an error in `eventBus.emit`
- **THEN** the error is caught and logged, pipeline continues normally

### Requirement: Build stage passes eventBus to RalphExecutor

`runPipelineBuildStage` SHALL pass `this.eventBus` to `RalphExecutor` via its context. `RalphExecutorContext` SHALL be extended with `workflowLogRepo`, `coderSessionRepo`, and `issueNumber` fields. These SHALL be forwarded to `_acpSessionRunner` (runAcpSession) calls.

#### Scenario: Build stage emits ralph_task_update
- **WHEN** `runPipelineBuildStage` is called and eventBus is available
- **THEN** `ralph_task_update` and `ralph_loop_progress` SSE events are emitted during Build

#### Scenario: Build stage coder sessions get eventBus
- **WHEN** RalphExecutor calls `_acpSessionRunner` for a task
- **THEN** the runner receives `eventBus`, `workflowLogRepo`, `coderSessionRepo`, and `issueNumber` from context

#### Scenario: Build stage SSE events use issue number
- **WHEN** `ralph_task_update` is emitted during Build stage
- **THEN** `issueId` field is `String(issueNumber)` (e.g., `"1"`), not UUID

### Requirement: RalphExecutor generates per-task executionId

`runPipelineBuildStage` SHALL construct `RalphExecutor` with `executionId: 'build-${issue.number}'`. Inside `runRalphLoop`, for each task, the system SHALL generate a unique `taskExecutionId = '${context.executionId}-${taskId}'` (e.g., `"build-1-T-001"`). The `ralph_task_update`, `coder_text_chunk`, and `coder_tool_call` events SHALL use `taskExecutionId`. The `ralph_loop_progress` event SHALL continue to use `context.executionId` (loop-level).

#### Scenario: Each Build task has unique executionId
- **WHEN** RalphExecutor processes task T-001, T-002, T-003 sequentially
- **THEN** `coder_text_chunk` events for T-001 have `executionId: "build-1-T-001"`
- **AND** `coder_text_chunk` events for T-002 have `executionId: "build-1-T-002"`
- **AND** frontend can distinguish agent text and tool calls per task

#### Scenario: Ralph loop progress uses loop-level executionId
- **WHEN** `ralph_loop_progress` is emitted during Build stage
- **THEN** `executionId` is `"build-1"` (without task suffix)
- **AND** loop-level progress is not tied to individual tasks

### Requirement: Live transcript convergence

Live session events SHALL update the same normalized transcript shape used by historical replay. SSE updates are an optimistic live view, and terminal or recovery lifecycle events SHALL reconcile the page with the canonical session detail transcript.

#### Scenario: Live tool updates merge in place

- **WHEN** live `coder_tool_call` start and update events arrive for the same id or inferable correlation key
- **THEN** the session page updates one existing logical tool part
- **AND** it does not append duplicate or orphan tool cards

#### Scenario: Live running state is restrained and accurate

- **WHEN** a session is actively streaming
- **THEN** only real non-terminal logical tools render as running
- **AND** pending or half-formed lifecycle fragments do not appear as separate visible tools

#### Scenario: Terminal events reconcile with persisted replay

- **WHEN** coder session completion, failure, timeout, cancellation, or recovery terminal events are observed
- **THEN** the page invalidates or refetches the session detail transcript
- **AND** the refetched historical transcript preserves equivalent visible order and grouping to the live view

#### Scenario: Live updates respect reader position

- **WHEN** the reader is near the bottom of the transcript
- **THEN** live text and tool updates follow the stream
- **WHEN** the reader has scrolled away from the bottom
- **THEN** live updates do not force-scroll and a new-content affordance is shown

### Requirement: REQ-PSE-001 Session liveness status is emitted to live clients

Session lifecycle event streams SHALL surface current session call liveness state so live clients can render the simplified session status.

#### Scenario: Probing status emitted
- **WHEN** a running session transitions to `probing`
- **THEN** an SSE event SHALL be emitted with session identifiers, status `probing`, `lastDataAt`, `probeSentAt`, and `probeDeadlineAt`

#### Scenario: Running recovery emitted
- **WHEN** a probing session receives valid new data and returns to `running`
- **THEN** an SSE event SHALL be emitted with status `running` and the updated `lastDataAt`

#### Scenario: Failure status emitted
- **WHEN** a session becomes `failed` due to probe timeout, probe send failure, protocol disconnect, or process exit
- **THEN** an SSE event SHALL be emitted with status `failed` and `failureReason`

#### Scenario: Recovery event not reused
- **WHEN** session liveness probing emits status changes
- **THEN** the event SHALL NOT use recovery-specific semantics such as `coder_recovery_status`

### Requirement: Live session events converge with replayed transcript display

Pipeline session events SHALL update the visible transcript in a way that converges with the canonical replayed session detail after refresh, completion, interruption, or recovery.

#### Scenario: Live tool updates do not create transcript duplication

- **WHEN** live tool start and update events arrive for an in-flight session
- **THEN** the visible transcript updates the existing logical tool part instead of appending duplicate or orphan rows

#### Scenario: Recovery and interruption remain readable

- **WHEN** recovery, interruption, cancellation, or failure events occur during a session
- **THEN** the transcript renders readable divider or error states appropriate to the event
- **AND** non-fatal interruption states are not all rendered as fatal red failures

#### Scenario: Refresh after live activity preserves transcript meaning

- **WHEN** a live session receives updates and the page later refetches the canonical detail response
- **THEN** the visible transcript keeps equivalent turn order, grouping, and changed-file sections after the refetch

### Requirement: Live transcript updates converge with replayed transcript display

Live SSE updates and replayed session detail SHALL converge on the same visible transcript structure so refresh does not materially change ordering, grouping, or tool identity.

#### Scenario: Live tool updates merge like replayed tools

- **WHEN** live tool start/update events arrive and the page later refetches canonical session detail
- **THEN** the transcript preserves equivalent tool identity, merge behavior, order, and grouping

#### Scenario: Terminal events reconcile to canonical transcript

- **WHEN** completion, failure, timeout, cancellation, or recovery terminal events are observed live
- **THEN** the frontend reconciles to the persisted transcript without losing text, tool updates, errors, or recovery markers

### Requirement: Persisted ordering fidelity improves for new session events

Newly persisted session stream events SHOULD preserve finer-grained ordering than second-level timestamps so transcript replay can represent reasoning, text, and tool interleaving more faithfully.

#### Scenario: New stream events retain sub-second ordering

- **WHEN** multiple transcript events are persisted within the same second for a new session
- **THEN** the stored event timestamps retain enough precision to distinguish their order
- **AND** existing historical sessions remain replayable without destructive migration

