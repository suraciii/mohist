## MODIFIED Requirements

### Requirement: SessionTimeline appends live SSE events
When an agent is actively running on the current issue, SessionTimeline SHALL subscribe to `plan_session_update`, `plan_round_start`, and `plan_round_complete` SSE events and append them to the current round in real-time. For Build stage, it SHALL also subscribe to `coder_text_chunk` and `coder_tool_call`.

#### Scenario: Agent starts new round while user is viewing
- **WHEN** the user is viewing the issue page and a new plan round starts
- **THEN** a new round section appears with the round label and begins accumulating agent output in real-time

#### Scenario: Agent text chunks stream into current round
- **WHEN** `plan_session_update` events with `sessionUpdate: 'agent_message_chunk'` arrive
- **THEN** the agent text in the current round updates with a typing cursor animation, using requestAnimationFrame-batched rendering

#### Scenario: Build stage coder events stream
- **WHEN** `coder_text_chunk` events arrive during Build stage (executionId starts with `build-`)
- **THEN** the text is appended to the current task's round section

#### Scenario: Plan round complete event updates progress
- **WHEN** a `plan_round_complete` event arrives during Plan stage
- **THEN** the corresponding step in `planProgress` is updated with completed/failed status and duration

### Requirement: Pipeline status timeline
The IssueDetailPage SHALL show a pipeline status timeline above SessionTimeline, displaying key events: pipeline start, each round completion with artifact produced, gate status, and any errors. When the plan stage is active, the timeline SHALL also render the `PlanProgressPanel` component showing real-time step progress.

#### Scenario: Pipeline in plan stage with gate awaiting
- **WHEN** the plan stage completes and is awaiting approval
- **THEN** the timeline shows: "Pipeline started" → "✓ Proposal" → "✓ Specs" → "✓ Design" → "✓ Tasks" → "✓ Self-review" → "⏸ Awaiting approval"

#### Scenario: Pipeline in plan stage mid-execution
- **WHEN** the plan stage is actively running and the user is viewing the issue
- **THEN** the `PlanProgressPanel` is rendered above the round timeline showing current step progress (e.g., "Plan Progress  2 / 5 completed")
