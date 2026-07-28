## Why

An Agent's saved execution definition is meant to make the same Agent behave consistently across Web, CLI, workflow, and event-driven entry points. Today launch requests and Issue variables can override its Runtime, while Skills are not carried into execution snapshots, so the configured Agent does not reliably determine what each run uses.

## What Changes

- Make the active Agent definition the sole source of Instructions, Runtime, Model, Variant, and Skills for every new AgentJob and `mohist/agent` workflow task attempt.
- Capture the resolved execution definition in the durable dispatch snapshot before work is offered; edits affect only later launches or retries, never an already accepted attempt or AgentSession.
- **BREAKING** Remove Runtime overrides from direct Agent launch requests and Issue-scoped variables/routing. Callers may continue to supply task text and contextual references, but cannot select a different execution backend for a named Agent.
- Deliver the snapshot's Skills to the selected runtime for both direct AgentJobs and workflow Agent actions, preserving the task prompt as the per-run work goal.
- Align API, CLI, Web, event-routing, and execution diagnostics with the single ownership rule; reject or remove inputs that attempt to override an Agent's execution definition.

## Capabilities
- `agent-execution-definition`: A named Agent's execution definition is the single, launch-time-fixed source of Instructions, Runtime, Model, Variant, and Skills for direct AgentJobs and `mohist/agent` workflow task attempts; callers can add task/context input but cannot override that definition.

## Impact

- **Server / Agent and Workflow**: Agent launch, routed launch, workflow Agent dispatch transformation, durable AgentJob and WorkDispatch snapshots, and Issue runtime-override resolution.
- **Runner**: AgentJob and workflow runtime requests consume the snapshotted Skills and selected runtime without rereading mutable Agent state.
- **API, CLI, and Web**: remove direct-launch Runtime selection and Issue-level Runtime override controls; retain Agent definition configuration as the only editing surface.
- **Docs and tests**: reconcile Agent configuration and action contracts, and cover entry-point-independent snapshot and retry behavior. No new external dependencies.
