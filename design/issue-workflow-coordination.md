---
purpose: "Cross-aggregate interactions between Issue, WorkflowRun, Runner, and Session as diagrams."
style:
  - "Vertical line = aggregate."
  - "Solid arrow = synchronous command. Dashed arrow = event."
  - "Commands named without the Async suffix or parameter list."
---

# Aggregate Coordination

## Conventions

- Aggregate names: `Issue` / `WorkflowRun` / `Runner` / `Session`
- Solid arrow: command (synchronous cross-aggregate call)
- Dashed arrow: event (triggers a downstream command)
- Single-aggregate transitions are listed in prose, not drawn

## 跨聚合事件→命令

| 事件 | 发送方 | 触发的命令 | 接收方 |
|---|---|---|---|
| `WorkflowRunCompleted` | WorkflowRun | `CompleteWork` | Issue |
| `WorkflowRunFailed` | WorkflowRun | `AbortWork` | Issue |
| `RunnerDisconnected` | Runner | (无 — Session 自决) | Session |

## Start

```text
Issue              WorkflowRun
  |                   |
  |--- StartWork ---->|
```

## Report (成功)

```text
Runner         WorkflowRun           Issue
  |               |                   |
  |--- Report --->|                   |
  |               |                   |
  |               · ··> WorkflowRunCompleted
  |               |                   |
  |               |--- CompleteWork ->|
```

## Report (失败)

```text
Runner         WorkflowRun           Issue
  |               |                   |
  |--- Report --->|                   |
  |               |                   |
  |               · ··> WorkflowRunFailed
  |               |                   |
  |               |--- AbortWork ---->|
```

## Stop (issue 在跑 workflow)

```text
Issue              WorkflowRun
  |                   |
  |--- Cancel ------->|
```

## Runner Disconnect

```text
Runner            Session
  |                 |
  · ··> RunnerDisconnected
  |                 |
                    | (fail sessions)
```

## 单一聚合 transition

```text
WorkflowRun 内部: Pause / Resume / Approve / Reject / Retry / Rerun
Issue 内部:      Archive / Unarchive / Reopen / Close
Runner 内部:     Register / Unregister / Heartbeat
```
