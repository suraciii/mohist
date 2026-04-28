## Context

Build 阶段任务超时由两层配置决定：

1. **Stage 总超时**：`workflow-loader.ts` 中 build stage 的 `timeout: 1800`（秒），通过 `workflow-controller.ts` 转为毫秒传入 ralph executor
2. **Per-task 下限**：`ralph-executor.ts:299` 中 `MIN_TASK_TIMEOUT_MS = 5 * 60 * 1000`

实际 per-task 超时 = `Math.max(stageTimeout / taskCount, MIN_TASK_TIMEOUT_MS)`。

Issue #41 的 6 任务 build stage：`1800 / 6 = 300s`，刚好等于 5 分钟下限。T-001 实际跑了 316 秒，差 11 秒完成。

## Goals / Non-Goals

**Goals:**
- 将 MIN_TASK_TIMEOUT_MS 从 5 分钟提高到 10 分钟
- 将 build stage 默认超时从 1800s 提高到 3600s
- 6 任务 build stage 每任务至少分配 600 秒

**Non-Goals:**
- 不改变 timeout 计算公式（`Math.max(division, floor)` 逻辑不变）
- 不引入 per-stage 或 per-task 的可配置 timeout 覆盖
- 不优化 agent 执行效率（那是 prompt/任务拆分的事）

## Decisions

### D1: 仅调整两个常量

改动限于两个数值常量，不修改计算逻辑或引入新配置项。

`ralph-executor.ts:299`：`5 * 60 * 1000` → `10 * 60 * 1000`
`workflow-loader.ts:52`：`timeout: 1800` → `timeout: 3600`

**Alternatives considered:**
- 添加 `minTaskTimeout` 可配置项 → 过度设计，当前只有一个 executor 使用此值
- 按 task 复杂度动态分配 timeout → 需要额外的复杂度评估机制，ROI 不够

### D2: 10 分钟下限而非更高

10 分钟对已观测到的任务足够（T-001 跑 316 秒，留近 2 倍余量）。更高下限（如 15 分钟）会在任务数多时导致 stage 总超时过长。

**Alternatives considered:**
- 15 分钟下限 → 6 任务需要 90 分钟 stage，收益递减
- 保持 5 分钟但提高 stage 总超时到 60 分钟 → 3600/6=600s 可以绕过问题，但下限仍然太低，其他 stage 或更少任务的场景仍然脆弱

## Risks / Trade-offs

- [Build stage 总时长增加] → 6 任务从最坏 30 分钟变为最坏 60 分钟。可接受，因为之前频繁超时重试反而更耗时
- [正在运行的 build stage 使用旧值] → 这些值是启动时读取的常量，正在运行的 session 不受影响

## Migration Plan

无需迁移。常量变更对已完成的 issue 无影响，对正在运行的 session 使用的是启动时的快照。重新启动 server 即生效。

## Open Questions

_(none)_
