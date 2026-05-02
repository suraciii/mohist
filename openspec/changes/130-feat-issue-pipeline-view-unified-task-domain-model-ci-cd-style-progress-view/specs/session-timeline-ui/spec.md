## REMOVED Requirements

### Requirement: SessionTimeline component renders rounds
**Reason**: Replaced by PipelineView's Step List component which renders Tasks and Checks sections per stage using the unified StageTask domain model.
**Migration**: Use PipelineView component instead. The Step List section within PipelineView provides equivalent functionality — rendering agent work items grouped by stage with expandable details.

### Requirement: SessionTimeline loads history from workflow_log API
**Reason**: PipelineView loads data from the structured `GET /api/issues/:number/executions` API which returns `StageTaskResult[]` and `CheckResult[]` per stage, eliminating the need to reconstruct rounds from workflow_log events.
**Migration**: Use `useIssueExecutions` hook which calls the executions API.

### Requirement: SessionTimeline appends live SSE events
**Reason**: PipelineView uses the unified `stage_task_update` SSE event for real-time updates, replacing the need to subscribe to multiple stage-specific event schemas.
**Migration**: PipelineView subscribes to `stage_task_update` events and updates the Step List in real-time.

### Requirement: Tool calls in timeline show expandable details
**Reason**: The tool-call-level detail view is out of scope for the PipelineView's Step List, which focuses on task-level progress. Tool call details remain accessible through the existing agent session viewer.
**Migration**: Use the agent session viewer for detailed tool call inspection.

### Requirement: Pipeline status timeline
**Reason**: Replaced by PipelineView's Stage Bar component which provides the same pipeline-level overview with stage status and timing.
**Migration**: The Stage Bar in PipelineView displays equivalent information: stage progression with status icons and durations.

### Requirement: Coder session rounds in Build stage
**Reason**: Replaced by PipelineView's Step List which renders Build tasks using the unified StageTask model, showing each task from tasks.json with status and timing.
**Migration**: Build tasks appear as Task items in the Step List's Tasks section.

### Requirement: Frontend uses RAF throttling for high-frequency events
**Reason**: The `stage_task_update` event is low-frequency (one per task state transition) unlike `plan_session_update` which streams text chunks. RAF throttling is no longer needed for the PipelineView's primary data path.
**Migration**: PipelineView updates directly from `stage_task_update` events without throttling. If detailed streaming is needed, use the existing `plan_session_update`/`coder_text_chunk` events with their existing RAF throttling.
