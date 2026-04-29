## Context

mohist 有成熟的 ACP 执行引擎 (`runAcpSession()`)、IssueService、EventBus 和 REST API 框架。当前所有 Agent 行为都是内置的（Plan → Build → Review Pipeline），用户无法自定义。本设计新增 Skill 系统，让用户通过 `.mohist/skills/` 下的 SKILL.md 定义自定义 Agent 行为，手动触发后自动创建 Issue 进入 Pipeline。

关键现有组件：
- `runAcpSession()` (`agent-runtime/acp-session.ts`): 接受 `{ cwd, task, eventBus, timeout, ... }` 启动 opencode ACP 子进程
- `IssueService` (`services/issue-service.ts`): `create({ projectId, title, body?, labels? })` 创建 Issue
- `EventBus` (`services/event-bus.ts`): 类型安全的 `emit<T>()`，事件类型定义在 `EventMap`
- `StateManager` (`server/state-manager.ts`): repo 工厂，新 repo 在此注册
- `HttpServer.addRouter()` + `server/index.ts` 汇编所有路由和依赖

当前 DB schema version = 15，新表在 `migrateToVersion16()` 中创建。

## Goals / Non-Goals

**Goals:**
- 端到端闭环：定义 SKILL.md → 触发执行 → 创建 Issue → 用户可审查
- 最小代码量，复用现有 ACP 引擎和 IssueService
- 异步执行，API 立即返回，后台运行 ACP session

**Non-Goals:**
- 定时调度（Phase 2, #100）
- 多来源 skill 加载（如 npm 包、URL）
- Skill 参数化/输入变量
- Skill 执行的并发控制（同时只跑一个 skill run，不排队）
- Web UI 展示（后续）

## Decisions

### D1: Frontmatter 解析手写，不引入新依赖

手写一个 ~30 行的 frontmatter 解析器：按 `---` 分隔提取 YAML 和 body。YAML 部分只用 `key: value` 单行格式，用正则 `/^(\w+):\s*(.+)$/m` 提取字段。

**Alternatives considered:**
- `gray-matter`（npm 包）：功能全但引入外部依赖，对 MVP 来说过重
- `js-yaml`：YAML 解析更健壮，但 frontmatter 只有 3 个简单字段，杀鸡用牛刀

### D2: 两个新 Service — SkillService 汇总加载+执行，不拆

一个 `SkillService` 类承担 skill 加载、注册到 DB、触发执行、创建 Issue。不拆成 Loader + Runner 两个 service，因为 MVP scope 小，拆分增加不必要的间接层。

`SkillService` 依赖：`SkillRepo`、`SkillRunRepo`、`IssueService`、`EventBus`、项目路径。

**Alternatives considered:**
- 拆成 `SkillLoader` + `SkillRunner`：过度工程化，MVP 只有一个加载入口和一个执行入口

### D3: 执行直接调用 `runAcpSession()`，不走 AgentRunnerService

`AgentRunnerService` 是为 Issue Pipeline 设计的（管理 Issue 的 stage/status 转换、worktree、并发队列）。Skill 执行更简单——只需要启动一个 ACP session、等结果、创建 Issue。直接调用 `runAcpSession()` 更直接，避免与 Issue Pipeline 的生命周期耦合。

**Alternatives considered:**
- 通过 `AgentRunnerService` 调度：需要传 issueId，而 skill run 时尚无 Issue；且 ARA 的并发控制、worktree 管理、stage 逻辑对 skill 无意义

### D4: 异步执行用 fire-and-forget Promise

`SkillService.run()` 创建 `skill_runs` 记录（status=`running`），立即返回记录，然后启动一个异步 Promise 执行 ACP session。Promise 完成后更新记录状态，emit 事件。不使用队列或 worker thread。

**Alternatives considered:**
- 队列 + worker：过度工程化，MVP 不需要并发控制和优先级
- 同步等待：API 会阻塞 30 分钟，不可接受

### D5: Issue title 从 ACP 输出第一行提取

ACP 返回的 text 通常以 Markdown 标题开头（如 `# Refactor X`）。取第一行、去掉 `# ` 前缀作为 Issue title，完整 text 作为 body。空输出时使用 fallback title `Skill result: <name>`。

### D6: DB Schema — 新增 2 张表，version 16

```sql
CREATE TABLE IF NOT EXISTS skills (
  id          TEXT PRIMARY KEY,
  name        TEXT UNIQUE NOT NULL,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  description TEXT NOT NULL DEFAULT '',
  prompt      TEXT NOT NULL DEFAULT '',
  dir_path    TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS skill_runs (
  id            TEXT PRIMARY KEY,
  skill_id      TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
  project_id    TEXT NOT NULL,
  status        TEXT NOT NULL DEFAULT 'running',
  output        TEXT,
  error         TEXT,
  issue_id      TEXT REFERENCES issues(id),
  started_at    TEXT NOT NULL,
  completed_at  TEXT
);

CREATE INDEX IF NOT EXISTS idx_skill_runs_skill_id ON skill_runs(skill_id);
```

UUID 主键（与现有 repos 一致使用 `uuid` 包）。

## Risks / Trade-offs

- **[ACP session 不可取消]** → MVP 不提供取消端点。用户只能等待超时。Phase 2 可加 `POST /api/skills/:name/runs/:id/cancel`
- **[Fire-and-forget 无全局追踪]** → server 重启后 running 状态的 run 会变成孤儿记录。MVP 接受此风险，Phase 2 可加 `expireRunning()` 恢复逻辑（类似 `QuestionRepo.expireAllPending()`）
- **[Frontmatter 解析脆弱]** → 手写解析只支持简单 `key: value` 格式。文档约束 SKILL.md 格式，后续可升级为 YAML parser
- **[同一 skill 可并发触发]** → MVP 不限制，每次触发独立运行。如果用户快速连点，可能同时跑多个 ACP session

## Migration Plan

1. 部署时 DB 自动升级到 v16（`initializeDatabase()` 检测 version 并执行增量迁移）
2. 无破坏性变更，不影响现有功能
3. 回滚：删除 v16 迁移代码，删表不影响现有数据

## Open Questions

- Skill 执行结果是否需要持久化到 `workflow_log` 表（与 Issue Pipeline 事件一致）？MVP 先不做，Phase 2 按需加。
