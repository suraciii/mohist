## Why

Mohist currently can re-dispatch workflow work that is already leased after grain activation or backlog recovery, which breaks the single-owner guarantee for active workflow tasks. This needs to be fixed now because duplicate runners can edit the same worktree and produce conflicting task results while the UI and durable lease state disagree about the active owner.

## What Changes

- Preserve active `WorkflowLease` ownership when a workflow grain activates instead of clearing the lease and allowing immediate re-dispatch.
- Refuse `GetWorkAsync` dispatch for a workflow that already has a valid active lease.
- Reconcile stale persisted leases through an explicit abandonment, expiration, or recovery path before making the workflow available again.
- Ensure duplicate `workflow_task_started` events for the same workflow work item cannot occur for different runners without an intervening abandon, failure, retry, or handoff event.
- Recover workflow backlog membership using durable project identity from workflow metadata or persisted workflow variables instead of defaulting projectless rows to the `default` backlog.
- Keep agent session read models, workflow leases, and agent activity/status APIs consistent about the active runner owner.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `workflow-engine`: Strengthen workflow dispatch requirements so active leases survive grain activation and block duplicate work assignment until explicitly released or recovered.
- `workflow-run`: Require durable workflow project identity to be recoverable from authoritative workflow data for backlog restoration.
- `workflow-agent`: Require active agent session and activity state to agree with workflow lease ownership for running work items.
- `pipeline-session-events`: Require task-start event streams to represent lease handoff explicitly before a different runner can start the same workflow work item.

## Impact

- Affected backend code includes `WorkflowGrain` activation and dispatch paths, workflow lease persistence/reconciliation, backlog recovery, runner unregister/timeout recovery paths, and agent session/activity read models.
- Affected storage includes `WorkflowLeases`, workflow run metadata/variables used for project recovery, backlog state, workflow events, and workflow agent session records.
- Affected APIs include runner work polling and agent activity/status endpoints, with no intended breaking changes to request or response shapes.
- Tests should cover grain reactivation with an existing lease, project-scoped backlog recovery when metadata annotations are missing, duplicate-dispatch prevention for the same work item, and owner consistency across leases, sessions, and activity status.
