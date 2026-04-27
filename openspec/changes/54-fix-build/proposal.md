## Why

Build 阶段任务频繁超时失败。Issue #41 的 T-001 实际运行 316 秒，但超时阈值仅 300 秒——差 11 秒完成就被 kill。根因是 build stage timeout（30 分钟）除以 6 个任务后刚好等于 MIN_TASK_TIMEOUT_MS（5 分钟），没有任何缓冲余量。

## What Changes

- `ralph-executor.ts:299` — `MIN_TASK_TIMEOUT_MS` 从 5 分钟（300s）提高到 10 分钟（600s）
- `workflow-loader.ts:52` — build stage timeout 从 1800s（30 分钟）提高到 3600s（60 分钟）
- 更新 `agent-timeout` spec 中 5 分钟下限为 10 分钟

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- **agent-timeout** — per-task timeout 下限从 5 分钟改为 10 分钟，build stage 总超时从 30 分钟改为 60 分钟

## Impact

- `packages/cli/src/openspec/ralph-executor.ts` — MIN_TASK_TIMEOUT_MS 常量
- `packages/cli/src/workflow/workflow-loader.ts` — build stage timeout 配置
- 向后兼容：仅增大超时值，不影响已完成或正在运行的 issue
- 6 任务 build stage 每任务分配：3600/6 = 600s（新下限），有充足缓冲
