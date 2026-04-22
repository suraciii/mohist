## Why

E2E walkthrough 暴露了 Plan stage 的两个可靠性问题：

1. **Agent 自行跳过产物生成**：`design.md` prompt 模板中写了 "you may skip this file and note why"，但 workflow controller 的 verify() 强制要求文件存在。Prompt 允许跳过、代码禁止跳过——矛盾导致 pipeline 必然失败。
2. **Server 重启后 pipeline 卡死**：`AgentRunnerService` 启动时检测到 recoverable issues 但只记录日志，不执行恢复。用户必须手动 resume + start。

这两个问题让基本的 E2E 流程无法走通。

## What Changes

- **修改 `design.md` prompt**：移除 skip 许可，改为要求 agent 即使对简单变更也必须生成 design.md（可以简化内容但不可省略文件）
- **修改 `self-review.md` prompt**：移除 "if it exists" 条件语，统一要求 review design.md
- **增加 server 启动恢复逻辑**：在 `server/index.ts` 启动时，将 recoverable issues 标记为 blocked 并回滚 stage 到 draft，避免卡死

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- `agent-spec-generation`: design prompt 从"可跳过"改为"必须生成"
- `pipeline-model`: server 启动时自动处理 orphaned active issues

## Impact

- **代码**: `src/agents/prompts/artifacts/design.md`, `src/agents/prompts/artifacts/self-review.md`, `src/server/index.ts`
- **APIs**: 无变化
- **依赖**: 无变化
- **系统**: 改善 Plan stage 成功率和 server 重启后的可用性
