## Context

`packages/cli/src/tools/spawn-coder.ts` 在 `executeCoderTask` 函数中调用了不存在的 `runAcpOneshot` 函数（第 78 行），但同一文件的 `createSpawnCoderTool` 函数（第 161 行）已经正确使用了 `runAcpSession`。这导致 `tsc` 编译失败，后端无法构建。

## Goals / Non-Goals

**Goals:**
- 修复 `executeCoderTask` 中的编译错误，使用已有的 `runAcpSession` 替代 `runAcpOneshot`
- 恢复 `npm run build` 正常编译

**Non-Goals:**
- 不改变 `runAcpSession` 的行为或接口
- 不重构 `spawn-coder.ts` 的其他部分

## Decisions

**直接替换为 `runAcpSession` 调用**：`runAcpSession` 接收一个 options 对象（`AcpSessionOptions`），与 `runAcpOneshot` 的位置参数不同。`createSpawnCoderTool` 中的调用（第 161 行）已经展示了正确的用法，直接复用该模式。

旧的 `runAcpOneshot` 调用：
```typescript
runAcpOneshot(cwd, task, timeout, issueId, workflowLogRepo, eventBus, projectId)
```

替换为：
```typescript
runAcpSession({ cwd, task, timeout, issueId, projectId, workflowLogRepo, eventBus })
```

注意 `executeCoderTask` 没有 `executionId`，因此不传该参数（为可选字段）。

## Risks / Trade-offs

- **无显著风险**：改动极小（仅一行调用），且与同文件内已有的正确用法一致
