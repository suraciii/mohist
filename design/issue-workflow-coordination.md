---
purpose: "Cross-aggregate interactions between Issue, WorkflowRun, Runner, and Session."
style:
  - "Sequence diagrams in mermaid."
  - "Synchronous command: solid arrow (->>). Asynchronous event: open arrow (-))."
  - "Commands named without the Async suffix or parameter list."
---

# Aggregate Coordination

## Conventions

- Aggregates: `Issue` / `WorkflowRun` / `Runner` / `Session`
- Solid arrow `->>`: command (synchronous cross-aggregate call)
- Open arrow `-)` : event (triggers a downstream command)
- Single-aggregate transitions are listed in prose, not drawn

## 跨聚合事件→命令

| 事件 | 发送方 | 触发的命令 | 接收方 |
|---|---|---|---|
| `WorkflowRunCompleted` | WorkflowRun | `CompleteWork` | Issue |
| `WorkflowRunFailed` | WorkflowRun | `AbortWork` | Issue |
| `RunnerDisconnected` | Runner | (无 — Session 自决) | Session |

## Start

```mermaid
sequenceDiagram
    participant Issue
    participant WorkflowRun
    Issue->>WorkflowRun: StartWork
```

## Report (成功)

```mermaid
sequenceDiagram
    participant Runner
    participant WorkflowRun
    participant Issue
    Runner->>WorkflowRun: Report
    rect rgb(240, 248, 255)
        Note over WorkflowRun,Issue: hook fires after WorkflowRunCompleted
        WorkflowRun-) Issue: WorkflowRunCompleted
        WorkflowRun->>Issue: CompleteWork
    end
```

## Report (失败)

```mermaid
sequenceDiagram
    participant Runner
    participant WorkflowRun
    participant Issue
    Runner->>WorkflowRun: Report
    rect rgb(255, 248, 240)
        Note over WorkflowRun,Issue: hook fires after WorkflowRunFailed
        WorkflowRun-) Issue: WorkflowRunFailed
        WorkflowRun->>Issue: AbortWork
    end
```

## Stop (issue 在跑 workflow)

```mermaid
sequenceDiagram
    participant Issue
    participant WorkflowRun
    Issue->>WorkflowRun: Cancel
```

## Runner Disconnect

```mermaid
sequenceDiagram
    participant Runner
    participant Session
    Runner-) Session: RunnerDisconnected
    Note over Session: fail sessions
```

## 单一聚合 transition

```text
WorkflowRun 内部: Pause / Resume / Approve / Reject / Retry / Rerun
Issue 内部:      Archive / Unarchive / Reopen / Close
Runner 内部:     Register / Unregister / Heartbeat
```
