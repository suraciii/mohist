## Why

A Workflow task that wants a configured Mohist Agent gets an Agent definition
reference: `WorkflowItemTranslator` resolves the Agent snapshot and rewrites the
task to an inline runtime Action (`mohist/opencode` / `mohist/pi`). The TaskRun
stays the work owner. No AgentJob is created, Agent readiness, workspace, and
concurrency admission apply only to direct launches, and Workflow results and
Session queries cannot locate each other's Job, Session, Input, and Turn
records. Users maintain two execution semantics for the same Agent.

The first slice of this change is already on master: the durable Server-side
handoff fence with a stable command identity, an immutable invocation holding
minted Job/Session/Input/Turn identifiers and a frozen Agent execution
snapshot, a definitive preflight rejection, and an acceptance receipt. Nothing
calls it yet. This change completes the path so an accepted Workflow handoff
executes as a real AgentJob through the same boundary as a direct Agent launch.

## What Changes

- Materialize the reserved AgentJob, AgentSession, first SessionInput, and
  first AgentTurn from a durable accepted receipt. Activation is idempotent,
  uses the frozen execution definition and minted identifiers, and cannot
  re-read mutable Agent configuration; later Agent edits affect only new
  invocations.
- Execute through the existing AgentJob admission and scheduling: shared Agent
  readiness, workspace resolution, concurrency limits, and Runner claim. No
  second queue, scheduler, or direct Runner-process control is introduced.
- Add typed transport between the Workflow handoff and the AgentJob
  participants, and return the AgentJob terminal result without using the
  Workflow task-report endpoint as an Agent transport channel.
- Add a Workflow-owned finalizer that consumes the AgentJob terminal and
  applies task completion effects — `expect`, `artifacts`, `setVars`, and
  recovery decisions — idempotently with completion-effect receipts. Workflow
  advancement stays Workflow-owned; the AgentJob owns the Agent execution
  lifecycle and result.
- Switch new `mohist/agent` dispatches to the handoff path only after
  participants, transport, and finalizer exist. Task input (`name`, `prompt`,
  `session`, `timeout`) is unchanged.
- Expose stable invocation status — queued, executing, completed, failed,
  cancelled, recovering — plus the Job/Session/Input/Turn identifiers and final
  result, so Workflow and Agent read surfaces locate the same execution from
  either side without parsing runtime transcript content.
- **BREAKING**: `mohist/agent` stops being an inline TaskRun-owned execution.
  Consumers that assumed a Workflow-only lifecycle or inline runtime output
  must adopt the AgentJob/AgentSession identifiers and the stable
  status/result contract.
- Leave other Workflow Actions, direct Agent launches, runtime adapters, and
  the Slack Bot / external Agent API surfaces unchanged and out of scope.

## Capabilities

- `workflow-agent-action`: Reusable configured-Agent execution from a Workflow
  task — durable handoff admission with a frozen execution snapshot,
  AgentJob/AgentSession/Input/Turn lineage, shared Agent admission and
  scheduling, typed transport, Workflow completion finalization, and the stable
  status, identifier, and result contract visible from both Workflow and
  Session surfaces.

## Impact

- **Server**: Workflow grains and services (`WorkflowGrain`,
  `WorkflowItemTranslator`, `WorkflowAgentHandoffGrain` activation), Agent
  launch coordination (`AgentJobGrain`, launch coordinator, concurrency
  permits), and persistence for participants and completion receipts.
- **APIs and read models**: Runner routes already distinguish `Workflow` and
  `AgentJob` work owners; Workflow and Agent read surfaces gain the
  cross-context invocation linkage and status projection.
- **Runner and runtime adapters**: execution boundary unchanged; Agent-backed
  dispatch already flows through the `AgentJob` owner kind.
- **Documentation**: `docs/actions/agent.md` and `docs/agent-sessions.md`
  ("Two Invocation Paths") move `mohist/agent` from a definition reference to
  a real delegation.
- **Tests**: grain specs for activation idempotency, shared admission,
  finalizer receipts, and dispatch cutover. No new external dependencies.
