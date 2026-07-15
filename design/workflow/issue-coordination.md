# Aggregate Coordination

Aggregates: `Issue`, `WorkflowRun`, `Runner`, `Session`.

Conventions: solid arrow `→` = synchronous command. `[Event]` = async event. Command branching from event = event-triggered.

## Event → Command

| Event | From | Command | To |
|---|---|---|---|
| WorkflowRunCompleted | WorkflowRun | CompleteIssue | Issue |
| WorkflowRunFailed | WorkflowRun | AbortWork | Issue |
| IssueCompleted / IssueCancelled / IssueReopened (sub-issue) | Issue | RecomputeComposite (aggregate status, start newly startable siblings) | Parent Issue |
| RunnerDisconnected | Runner | — | Session self-decides |

## Interactions

```
Issue → StartWork → WorkflowRun

Parent Issue → StartWork → each startable sub-issue (composite advance; parent has no WorkflowRun)

Runner → Report → WorkflowRun ──[WorkflowRunCompleted]──→ Issue.CompleteIssue
Runner → Report → WorkflowRun ──[WorkflowRunFailed]─────→ Issue.AbortWork

Issue → Cancel → WorkflowRun
Runner ──[RunnerDisconnected]──→ Session (fails affected sessions)

WorkflowRun: Pause, Resume, Approve, Reject, Retry, Rerun
Issue: Archive, Unarchive, Reopen, Close
Runner: Register, Unregister, Heartbeat
```
