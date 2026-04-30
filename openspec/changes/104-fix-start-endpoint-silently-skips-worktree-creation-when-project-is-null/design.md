## Context

`packages/cli/src/api/issues.ts` 中 5 个 POST endpoint（start/reopen/approve/reject/messages）共享同一模式：

```typescript
const project = projectService.getById(projectId);
let worktreePath = process.cwd();
if (worktreeManager && project) {
  worktreePath = await worktreeManager.create(project.path, project.name, issue.number, project.baseBranch);
}
```

当 `project` 为 null 时，`worktreePath` 保持 `process.cwd()`（主仓库路径），agent 在非隔离环境中工作。`propose.ts:59-66` 已有正确的 null 检查模式可参考。文件已有 `log` 实例（L20-23: `const log = Log.create({ service: 'issue' })`）。

## Goals / Non-Goals

**Goals:**
- 5 个 endpoint 在 project 为 null 时返回 404 并记录 warn 日志
- 统一修复模式，与 `propose.ts` 一致

**Non-Goals:**
- 不重构提取共享 helper（5 处改动足够小，不需要抽象）
- 不处理 worktreeManager 为 null 的情况（独立关注点，当前已有隐式 fallback）
- 不修复其他 `projectService.getById()` 调用点（多数已有 null 检查或用途不同）

## Decisions

### D1: 在 worktreePath 赋值之前插入 null 检查

在每个 endpoint 的 `const project = projectService.getById(projectId)` 之后、`let worktreePath` 之前，插入与 `propose.ts:60-66` 一致的 null 检查：

```typescript
if (!project) {
  log.warn('Project not found', { projectId, issueNumber: number });
  return c.json({ success: false, error: 'Project not found' }, 404);
}
```

**Alternatives considered:**
- 提取 `resolveProjectOr404` helper — 5 处调用不值得抽象，且每个 endpoint 的 early return 上下文不同（部分在 `if (agentRunner)` 块内）
- 在 `projectService.getById` 层面抛异常 — 改动范围过大，影响所有调用方

### D2: 使用 404 而非 400

与 `propose.ts:65` 保持一致。project ID 来自 server 内部状态（`getCurrentProjectId()`），如果查不到属于数据不一致，404（资源不存在）语义更准确。

### D3: start endpoint 的 null 检查放在 stage transition 之前

`start` endpoint 在 L431 获取 project 后，L437 执行 `issueService.transitionToStage(issue.id, Stage.Plan)`。null 检查必须放在 transition 之前，避免 project 不存在时仍推进 issue 状态。

## Risks / Trade-offs

- [已有的 running session 如果 projectId 对应的 project 被删除，reject/approve/messages 会返回 404] → 这是正确行为，不应操作无效 project 的 issue
- [start endpoint null 检查在 stage transition 前，如果 project 不存在但 issue 已 transition 到 Plan] → 不会发生，因为检查在 transition 之前

## Migration Plan

无需迁移。修复是纯防御性检查，正常路径行为不变。

## Open Questions

(none)
