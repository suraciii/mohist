---
purpose: "Cross-aggregate interactions between Issue, WorkflowRun, Runner, and Session."
style:
  - "ASCII text diagrams."
  - "Synchronous command: solid arrow (------>)."
  - "Asynchronous event: floating `[EventName]` label emitted by an aggregate."
  - "When an event triggers a command, the command arrow branches from the event label."
  - "Commands named without the Async suffix or parameter list."
---

# Aggregate Coordination

## Conventions

- Aggregates: `Issue` / `WorkflowRun` / `Runner` / `Session`
- Solid arrow `------>`: command (synchronous cross-aggregate call)
- Floating `[EventName]`: event emitted without a specific aggregate target
- Command arrow branching down from an event label: the command is triggered by that event
- Single-aggregate transitions are listed in prose, not drawn

## 跨聚合事件→命令

| 事件 | 发送方 | 命令 | 接收方 |
|---|---|---|---|
| `WorkflowRunCompleted` | WorkflowRun | `CompleteIssue` | Issue |
| `WorkflowRunFailed` | WorkflowRun | `AbortWork` | Issue |
| `RunnerDisconnected` | Runner | (无 — Session 自决) | Session |

## Start

```text
Issue       WorkflowRun
 |             |
 |  StartWork  |
 |------------>|
 |             |
```

## Report (成功)

```text
Runner      WorkflowRun            Issue
 |             |                    |
 |   Report    |                    |
 |------------>|                    |
 |             |                    |
 |       [WorkflowRunCompleted]     |
 |             |      |             |
 |             |      | CompleteIssue
 |             |      v             |
 |             |------------------->|
 |             |                    |
```

## Report (失败)

```text
Runner      WorkflowRun            Issue
 |             |                    |
 |   Report    |                    |
 |------------>|                    |
 |             |                    |
 |       [WorkflowRunFailed]        |
 |             |      |             |
 |             |      |  AbortWork  |
 |             |      v             |
 |             |------------------->|
 |             |                    |
```

## Stop (issue 在跑 workflow)

```text
Issue       WorkflowRun
 |             |
 |   Cancel    |
 |------------>|
 |             |
```

## Runner Disconnect

```text
Runner                    Session
 |                          |
 |   [RunnerDisconnected]   |
 |~~~~~~~~~~~~~~~~~~~~~~~~~>|
 |                          |
 |      (Session fails      |
 |       affected sessions) |
 |                          |
```

## 单一聚合 transition

```text
WorkflowRun 内部: Pause / Resume / Approve / Reject / Retry / Rerun
Issue 内部:      Archive / Unarchive / Reopen / Close
Runner 内部:     Register / Unregister / Heartbeat
```
