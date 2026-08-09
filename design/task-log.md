# Task Log

Task execution log. Like GitHub Actions step log: collapsible, line-by-line, streamable.

## Problem

stdout/stderr from git clone, rebase, branch check, workspace cleanup is discarded. Only `runCommand` captures `combinedOutput`. Users see final status, not the process.

## Boundary

TaskLog belongs to Runner domain. Never in WorkflowRun.

```text
POST /api/{ownerKind}/{ownerId}/work/{workId}/task-log  -> TaskLogStore -> independent
POST /api/runner/{runnerId}/report                       -> WorkResult   -> WorkflowGrain
```

Owner = `workflow-runs` | `agent-jobs`. Same pattern as artifact upload.

## Model

```text
TaskRun (existing, in WorkflowRun)
 |-- status / message / output       <- final result
 |-- Artifacts                       <- outputs
 |-- AgentSession transcript         <- agent conversation
 `-- TaskLog                         <- execution trace
      `-- LogEntry[]
           |-- seq        monotonic, cursor pagination + jump anchor
           |-- timestamp
           |-- source     workspace-prep | action:rebase | cleanup | ...
           `-- text
```

No stdout/stderr split. Same as GA.

## Collection (runner side)

### Single funnel

```ts
ActionContext.log.write(source, text) -> seq
```

All output enters here. Secret masking, sequence, buffering. One method.

### Secret masking

```ts
write(source, text): number {
  const masked = this.maskSecrets(text)
  const seq = ++this._seq
  this._collector.append({ seq, timestamp: this._clock.now(), source, text: masked })
  return seq
}
```

### runCommand line-by-line

```ts
interface RunCommandOptions {
  onLine?: (line: string) => void   // stdout+stderr merged
}
```

Must guarantee: capture last line without trailing newline, drain after process exit, timeout-kill stuck read.

### Collector

```text
TaskLogCollector (per work)
  buffer: LogEntry[]           <- append only
  flush()                      <- batch POST (end-of-task or periodic)
  capacity limit               <- drop head, keep tail (error context)
```

## Report channel

Separate endpoint. Never in report payload.

| Phase | When |
|---|---|
| Phase 1 | batch flush before report. full log on task complete. |
| Phase 2 | periodic flush + SignalR. real-time, best-effort. store is authority. |

## Storage (server side)

```text
TaskLogEntries
  Id (PK)
  OwnerKind       "workflow" | "agent-job"
  OwnerId         workflowRunId or agentJobId
  WorkId
  Seq             monotonic per task
  Timestamp
  Source
  Text
```

No stream column. Index: (OwnerKind, OwnerId, WorkId, Seq).

Query: `GET /api/projects/{pid}/issues/{num}/workflow/tasks/{tid}/logs?cursor=&limit=`

Capacity: cap per task (e.g. 256KB / 5000 lines). Truncate head, keep tail. Seq stays monotonic.

## Relationship to existing

| Concept | Answers | Domain |
|---|---|---|
| TaskLog | what happened during execution | Runner |
| Transcript | what agent said | Session |
| Artifact | what files were produced | Workflow |
| task output | structured result | Workflow |
