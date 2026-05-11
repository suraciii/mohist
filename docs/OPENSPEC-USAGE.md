# OpenSpec Workflow Usage Guide

Mohist 的 OpenSpec 工作流：结构化 Change 产物 + Ralph 式任务执行。

## 概述

```
Plan → Build → Check → Integrate → Done
```

- **Plan**: AI 生成 Change 产物 (proposal, design, specs, tasks.json)，含自审
- **Build**: Ralph 式任务循环执行
- **Check**: AI 代码审查 + merge-ready 检查
- **Integrate**: 规格同步 + 归档 + 合并 + 集成后健康检查

## 核心概念

### Change

Change 是结构化产物目录：

```
openspec/changes/{issue-number}-{slug}/
├── proposal.md        # 为什么做这个变更，解决什么问题
├── design.md          # 技术方案和决策
├── specs/             # 按能力拆分的详细规格
│   ├── capability-a/spec.md
│   └── capability-b/spec.md
├── tasks.json         # 带执行状态的任务列表 (Ralph 式)
├── self-review.md     # Plan 阶段 Agent 自审报告
├── review.md          # Check 阶段 AI 审查报告
└── session-memories/  # 任务执行的学习记录
    └── T-001.json
```

### Ralph Loop

Ralph 式执行按顺序遍历 `tasks.json` 中的任务：

1. 按 order 选择下一个待执行任务
2. 组装完整上下文 (proposal + design + spec + learnings)
3. 使用 AI agent 执行
4. 验证验收标准
5. 存储学习记录
6. 重复直到所有任务完成

## 命令

### `mo propose <issue-number>`

为 Issue 创建 Change 并启动 Plan 阶段。

```bash
# 为 Issue #42 创建 Change
mo propose 42

# 强制重建（覆盖已有 Change）
mo propose 42 --force
```

该命令：
1. 创建 `openspec/changes/{issue-number}-{slug}/`
2. 启动 AI agent 探索代码库
3. Agent 生成 proposal, design, specs
4. Agent 执行自审（最多 3 次迭代）
5. 自审通过后生成 `tasks.json`

### `mo issue resume <number>`

从断点恢复 Issue 执行。

```bash
# 恢复 Issue #42
mo issue resume 42

# 跳过 Plan review 直接进入 Build
# （在手动修复 Plan 产物后使用）
mo issue resume 42 --skip-to-review
```

## 工作流阶段

### Plan 阶段

Agent 探索 Issue 和代码库，生成：

- **proposal.md**: 动机、目标、非目标
- **design.md**: 架构决策、权衡
- **specs/**: 每个能力的详细需求
- **tasks.json**: 从 specs 派生的任务列表
- **self-review.md**: Agent 自审 report（最多 3 次迭代）

Plan gate 包含用户审批和健康门控（`npm run typecheck`）。

### Build 阶段

Ralph loop 执行 `tasks.json` 中的任务：

- 任务按 `order` 字段顺序执行
- 每个任务获得完整上下文 (proposal, design, spec, learnings)
- 失败分析后带有失败上下文重试
- 任务状态通过每个任务上的 `passes`/`attempts`/`error` 追踪

Build gate 包含健康门控（`npm run build`）和全部任务完成检查。

### Check 阶段

- AI agent 审查代码，生成 `review.md`
- 合并就绪检查（worktree 快进合并）
- 用户审批后进入 Integrate

### Integrate 阶段

- 增量规格同步到主规格
- Change 归档到 `openspec/changes/archive/`
- 压缩合并到目标分支
- 集成后健康门控（`npm run build && npm test`）

## 示例工作流

### 1. 启动服务

```bash
mo server start
```

### 2. 创建 Change

```bash
mo propose 42
```

Agent 探索 Issue 并生成产物。

### 3. 审查产物

```bash
# 检查生成的产物
cat openspec/changes/42-my-issue/proposal.md
cat openspec/changes/42-my-issue/tasks.json

# 满意后审批
mo issue approve 42
```

### 4. Build 自动执行

Agent 运行 Ralph loop，执行 `tasks.json` 中的每个任务。

### 5. Check 审查

```bash
# 自动测试运行
# 查看变更
mo issue diff 42

# 查看详情
mo issue show 42

# 实现正确则审批
mo issue approve 42
```

### 6. Integrate 自动完成

规格同步、归档、合并自动完成。

## 任务状态

在 `tasks.json` 中追踪执行状态——每个任务有 `passes`、`attempts`、`error` 字段：

```bash
cat openspec/changes/42-my-issue/tasks.json
```

```json
{
  "version": 1,
  "tasks": [
    {"id": "T-001", "title": "...", "passes": true, "attempts": 1, "error": null},
    {"id": "T-002", "title": "...", "passes": true, "attempts": 1, "error": null},
    {"id": "T-003", "title": "...", "passes": false, "attempts": 1, "error": "Type error: ..."}
  ]
}
```

## Session Memories

任务执行的学习记录存放在：

```
openspec/changes/{change}/session-memories/{task-id}.json
```

每个文件包含：

```json
{
  "task_id": "T-001",
  "timestamp": "2024-01-15T10:30:00Z",
  "insights": ["Constraint discovered: API rate limit"],
  "adjustments": ["Task T-002 should handle retries"],
  "success": true,
  "execution_summary": "Implemented auth endpoint"
}
```

这些学习记录会传递给后续任务。

## 恢复场景

### Build 任务 T-003 失败

1. Agent 带失败上下文重试（最多 2 次）
2. 仍失败则暂停
3. 用户手动修复问题
4. 用户运行 `mo issue resume 42 --skip-to-review`
5. Build 从 T-003 恢复

### Plan 自审失败

1. 3 次迭代后仍未通过，Plan 阶段失败
2. 用户手动编辑产物
3. 用户运行 `mo issue resume 42 --skip-to-review` 继续

## 文件位置

| 路径 | 说明 |
|------|------|
| `openspec/changes/` | 活跃的 changes |
| `openspec/changes/archive/` | 已完成的 changes |
| `~/.mohist/mohist.db` | SQLite 数据库 |
| `~/.mohist/logs/` | 服务日志 |
| `~/.mohist/config.jsonc` | 配置文件 |