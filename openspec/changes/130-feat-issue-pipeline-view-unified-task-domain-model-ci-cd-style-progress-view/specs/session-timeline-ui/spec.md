## REMOVED Requirements

### Requirement: SessionTimeline component renders rounds
**Reason**: Replaced by PipelineView's Step List which renders unified StageTask entries instead of round-based sections.
**Migration**: Use PipelineView component with StageBar + StepList. Tasks section replaces round sections. Each StageTask is rendered with status icon + title + duration, replacing the collapsible round UI.

### Requirement: SessionTimeline loads history from workflow_log API
**Reason**: PipelineView loads historical data from `GET /api/issues/:number/executions` API which returns structured StageTaskResult[] directly, eliminating the need to reconstruct rounds from workflow_log events.
**Migration**: PipelineView fetches from `GET /api/issues/:number/executions` for completed stage data. Live updates come from `stage_task_update` SSE events.

### Requirement: SessionTimeline appends live SSE events
**Reason**: Replaced by PipelineView subscribing to `stage_task_update` unified SSE event. The old per-stage event subscriptions (plan_session_update, coder_text_chunk, coder_tool_call) are no longer needed for task-level progress display.
**Migration**: PipelineView subscribes to `stage_task_update` for all stage task progress. The old events are still emitted but PipelineView does not depend on them.

### Requirement: Tool calls in timeline show expandable details
**Reason**: Tool call details are out of scope for the Pipeline View's task-level progress display. Task expansion shows artifact summaries, not raw tool call logs.
**Migration**: Tool call details remain accessible through the existing agent session detail UI if needed. The Pipeline View focuses on task status and artifacts.

### Requirement: Pipeline status timeline
**Reason**: The Pipeline status timeline is absorbed by the Stage Bar component in PipelineView, which provides the same information (stage progression + status) in a more compact horizontal layout.
**Migration**: StageBar component in PipelineView shows stage progression with status icons and timing, replacing the vertical timeline.

### Requirement: Coder session rounds in Build stage
**Reason**: Build stage tasks are now rendered as StageTask entries in the Step List, using the unified StageTask model. The separate "coder session rounds" concept is replaced by Build tasks with source='dynamic'.
**Migration**: Build tasks appear in the Step List's Tasks section as regular StageTask entries, with the same status icons and expandable artifacts as Plan/Check tasks.

### Requirement: Frontend uses RAF throttling for high-frequency events
**Reason**: RAF throttling requirement is now specified in the `pipeline-view` capability spec, applied to `stage_task_update` events specifically.
**Migration**: Use the `usePipelineView` hook with RAF-based throttling for `stage_task_update` events, matching the same 100ms batching pattern.
