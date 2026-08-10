## Why

Workflow profiles can reference a configured Agent, but the current path translates that call into an inline runtime Action owned entirely by the WorkflowRun. It therefore cannot provide the durable AgentJob and independent AgentSession lineage, shared admission safety, or lifecycle and result visibility that direct Agent execution already requires.

## What Changes

- Change `mohist/agent` into a reusable Workflow Action that selects an active project Agent and accepts a workflow task prompt plus its permitted context.
- Create a stable AgentJob and independent AgentSession for each accepted invocation, with locatable initial SessionInput and AgentTurn references. The Workflow task and Agent execution must be able to correlate their own records without parsing runtime transcript contents.
- Freeze the selected Agent execution definition and accepted input/workspace facts for the invocation. Later edits to the Agent definition must not change queued or running work; only a new invocation can observe those edits.
- Reuse the canonical Agent readiness, workspace resolution, concurrency, and Runner admission rules used by direct Agent launches. The Workflow Action must not create a second queue or control Runner internals directly.
- Project the invocation through stable Workflow and Agent read surfaces with distinguishable queued, executing, completed, failed, cancelled, and recovering states, plus a stable final result. Workflow advancement remains Workflow-owned, while AgentJob owns the Agent execution lifecycle and result.
- **BREAKING**: Existing `mohist/agent` consumers that assume a TaskRun-only lifecycle or inline runtime output must adopt the AgentJob/AgentSession identifiers and stable status/result contract.
- Leave other Workflow Actions unchanged. Slack Bot behavior, a general external Agent API, and direct Runner-process control remain out of scope.

## Capabilities

- `workflow-agent-action`: Reusable configured-Agent execution from a Workflow, including input validation, Job/Session/Input/Turn lineage, immutable execution snapshots, shared admission, lifecycle states, recovery visibility, and stable results.

## Impact

- **Server:** Workflow task translation and reporting, AgentJob and AgentSession creation/linkage, Agent execution snapshot resolution, readiness/workspace/concurrency admission, recovery coordination, durable projections, and persistence for the cross-context lineage.
- **Runner and runtime adapters:** Agent-backed dispatch and result reporting must carry the stable Job/Session/Input/Turn references while continuing to use the existing runtime execution boundaries.
- **Workflow and Agent read APIs:** Add or extend projections so a Workflow invocation and its Agent execution can be queried from either side without exposing internal Runner or transcript details.
- **Workflow definitions and validation:** Define the supported `mohist/agent` inputs and output/status contract; existing inline runtime Actions and unrelated Action contracts remain unchanged.
- **Tests and documentation:** Add contract coverage for Agent selection, snapshot isolation, admission, lineage, lifecycle/recovery states, and stable results. No new external dependency is expected.
