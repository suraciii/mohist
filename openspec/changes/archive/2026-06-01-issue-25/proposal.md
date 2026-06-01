## Why

Workflow scheduling state can currently retain stale backlog entries and leases for workflows that are paused, failed, cancelled, completed, or already claimed. This creates conflicting runtime state that makes runner diagnostics unreliable and can cause runner capacity to be spent repeatedly on work that is no longer runnable.

## What Changes

- Enforce a single authoritative scheduling state per workflow: waiting without a lease, running with an active lease, or absent when paused or terminal.
- Release backlog claims and active leases when an issue is cancelled or its workflow is paused.
- Remove completed and failed workflows from persisted backlog state when they reach terminal workflow states.
- Prevent a workflow from being persisted in both `Waiting` and `Running` buckets for the same backlog.
- Repair stale runner claims when polling claims a workflow but the workflow has no work to return.
- Reconcile persisted backlog and lease state during startup recovery by removing paused, terminal, cancelled, or no-work entries instead of only re-registering runnable workflows.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `workflow-engine`: Workflow scheduling must maintain consistent backlog and lease invariants across pause, cancellation, terminal transitions, polling, and startup recovery.
- `workflow-run`: Workflow run lifecycle transitions must remove non-runnable runs from scheduling state and clear active work ownership when the run becomes paused or terminal.
- `agent-pool`: Runner polling must repair or release stale claims when a claimed workflow cannot provide runnable work.

## Impact

- Affects backend workflow scheduling grains, especially backlog registration/claim/release behavior and persisted backlog state.
- Affects workflow pause, cancellation, failure, and completion paths that currently leave scheduling state behind.
- Affects runner polling and assignment tracking when claimed workflows return no work.
- Affects startup backlog recovery and workflow lease reconciliation.
- Requires backend tests for active-lease cancellation cleanup, terminal workflow cleanup, waiting/running deduplication, and poll-claim-no-work repair.
