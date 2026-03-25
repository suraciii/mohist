## Context

当前 Crawlph 使用 CLI-Server 架构，Server 管理 workflow 逻辑和 agent 执行。MVP 阶段我们希望在本地快速迭代，不依赖 GitHub API。

技术栈：
- TypeScript
- Node.js 18+
- better-sqlite3
- Express (Server)
- Commander (CLI)

## Goals / Non-Goals

**Goals:**
- 扩展现有 SQLite schema（添加 labels、comments）
- 新增本地 Issue CRUD CLI 命令
- 保留 CLI-Server 架构
- 保留现有 stage/status 模型
- 为后续 GitHub 同步预留空间

**Non-Goals:**
- GitHub 同步（后续 capability）
- 移除 Server（Server 是核心组件）
- 改变技术栈
- 多用户支持

## Decisions

### 1. 架构保持不变

**决定**: 保留 CLI → Server → SQLite 架构

```
CLI (thin) ──HTTP──▶ Server ──▶ SQLite
                         │
                         ├── StateManager
                         ├── TaskQueue
                         ├── Workflow 逻辑
                         └── Agent 执行
```

**理由**:
- Server 管理 workflow 和 agent 执行，是核心组件
- CLI 通过 HTTP API 与 Server 通信
- MVP 阶段只是数据源从 GitHub 改为本地

### 2. Labels 与 Stage 独立

**决定**: Labels 和 Stage 各自独立管理

```
Issue:
  stage: Stage (枚举)     -- workflow 阶段
  status: IssueStatus     -- workflow 状态
  labels: string[] (JSON) -- 自由标签，补充分类
```

**理由**:
- Stage/Status 是 workflow 核心概念，保持不变
- Labels 提供灵活分类能力（如 `bug`, `feature`, `priority:high`）
- 两者独立，不相互影响

### 3. Comments 无 Author

**决定**: Comments 表不包含 author 字段

```sql
CREATE TABLE comments (
  id          TEXT PRIMARY KEY,
  issue_id    TEXT NOT NULL REFERENCES issues(id),
  body        TEXT NOT NULL,
  created_at  TEXT NOT NULL
);
```

**理由**:
- MVP 单用户场景
- 简化实现
- 后续如需多用户，可添加 author 字段

### 4. Schema 扩展策略

**决定**: 扩展现有 migrations.ts

```sql
-- 扩展 issues 表
ALTER TABLE issues ADD COLUMN labels TEXT DEFAULT '[]';

-- 新增 comments 表
CREATE TABLE comments (
  id          TEXT PRIMARY KEY,
  issue_id    TEXT NOT NULL REFERENCES issues(id),
  body        TEXT NOT NULL,
  created_at  TEXT NOT NULL
);
```

**理由**:
- 保留现有数据
- 渐进式迁移
- labels 默认空数组

### 5. Issue 显示格式

**决定**: `project#number` 格式

**示例**: `my-app#1`, `crawlph#42`

**理由**:
- 跨项目唯一
- 与 GitHub 格式一致
- 便于后续 GitHub 同步

### 6. CLI 命令设计

**决定**: 保留 workflow 命令，新增 CRUD 命令

```bash
# 保留（workflow 操作）
ph issue start <id>      # 开始处理
ph issue pause <id>      # 暂停
ph issue resume <id>     # 恢复

# 新增（CRUD 操作）
ph issue create "title" [-l label]...
ph issue update <id> [--title "..."] [--body "..."] [-l +label] [-l -label]
ph issue close <id>
ph issue reopen <id>
ph issue comment <id> "text"
ph label list
```

**理由**:
- start/pause/resume 是 workflow 控制，保留
- create/update/close/comment 是数据操作，新增

### 7. MVP 范围

**决定**: MVP 不包含 PR 管理

**包含**:
- Issue CRUD
- Labels 管理
- Comments
- Workflow 操作

**不包含**:
- PR 管理
- GitHub 同步
- Webhook

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| Labels 查询效率低（JSON） | MVP 数据量小，够用；后续可迁移到关联表 |
| 无 GitHub 同步 | 后续独立 capability |
| 单用户限制 | MVP 够用，后续可扩展 |

## Migration Plan

1. 扩展 migrations.ts（添加 schema version 2）
2. 现有 issues.labels 默认 `[]`
3. 现有数据不受影响
