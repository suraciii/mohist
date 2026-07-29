## Why

AgentJob and Workflow work currently follow different scheduling models: WorkflowRun is its own dispatch ledger, while AgentJob work is copied into Runner-owned storage and reconciled later. This duplicate ownership creates avoidable recovery paths and races, conflicting with Mohist's single state authority and fact-only Runner boundary.

## What Changes

- Make both WorkflowRun and AgentJob the durable dispatch ledger for their own work, including assignment, runnable state, and reconstructable dispatch data.
- Deliver all work through the runner's poll-driven pull protocol, with redelivery derived from owner state rather than Runner-side work staging.
- Remove Runner-owned AgentJob work snapshots, outstanding-work ledger records, push assignment, and reconciliation logic.
- Apply shared capacity, runner-loss closeout, stale-report acknowledgement, and unavailable-runner handling consistently to both work owners.

## Capabilities

- `work-dispatch-ledger`: Owner-led scheduling, poll delivery, recovery, capacity, and reporting behavior for WorkflowRun and AgentJob work.

## Impact

- Server Agent, Workflow, and Runner scheduling paths, including AgentJob state/projections, `DispatchService`, `RunnerGrain`, runner-work persistence, and report/closeout handling.
- Runner polling and report behavior remains the execution-plane interface, but AgentJob dispatches move from push assignment to the shared pull flow.
- Runner status and task-log read models must obtain active-work ownership from the work owners rather than Runner-held work records.
- No new external dependencies or user-facing API surface are required.
