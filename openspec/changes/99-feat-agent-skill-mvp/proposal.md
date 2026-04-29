## Why

mohist 有成熟的 ACP 执行引擎和 Issue Pipeline，但用户无法自定义 Agent 的行为。用户需要在项目中定义 SKILL.md（描述"做什么"），手动触发执行后，Agent 自动分析项目并创建 Issue 进入 Pipeline 审查。这是 Agent Service 的第一个垂直切片——端到端闭环，用户定义 skill → 触发 → 得到 Issue。

## What Changes

- 新增 Skill 加载器：扫描 `.mohist/skills/` 目录，解析 SKILL.md（含 YAML frontmatter 的 name/description/prompt 字段）
- 新增 Skill 执行引擎：复用现有 `runAcpSession()`，以 skill 的 prompt 作为 task 注入，在项目 worktree 中执行
- 新增输出处理：skill 执行完成后，解析 ACP 输出，调用 `IssueService.create()` 创建 Issue（stage=`backlog`），进入 Pipeline 供用户审查
- 新增 SQLite 表：`skills`（注册的 skill 元数据）、`skill_runs`（执行历史记录）
- 新增 REST API 端点：`GET /api/skills`（列出）、`POST /api/skills/:name/run`（触发执行）、`GET /api/skills/:name/runs`（执行历史）
- 新增 EventBus 事件：`skill_started`、`skill_completed`、`skill_failed`
- 新增内置示例 skill（如 `analyze-codebase`）— 后续可加，不在 MVP 任务范围内

## Capabilities

### New Capabilities

- `skill-loader`: 扫描并解析 `.mohist/skills/` 目录下的 SKILL.md，提取 frontmatter 元数据（name、description、prompt），注册到系统
- `skill-execution`: 编排 skill 的手动执行——复用 ACP session 执行 prompt，捕获输出，调用 IssueService 创建 Issue，记录执行历史
- `skill-api`: REST API 端点——列出已注册 skills、触发执行、查看执行历史

### Modified Capabilities

- `event-bus`: 新增 `skill_started`、`skill_completed`、`skill_failed` 事件类型
- `http-api`: 新增 `/api/skills` 路由组（3 个端点）

## Impact

- **新增文件**：`src/services/skill-service.ts`、`src/db/skill-repo.ts`、`src/db/skill-run-repo.ts`、`src/api/skills.ts`
- **修改文件**：`src/db/migrations.ts`（新增 2 张表）、`src/services/event-bus.ts`（新增事件类型）、`src/api/index.ts`（注册路由）、`src/server/`（注册 API）
- **依赖**：复用现有 `runAcpSession()`（agent-runtime）、`IssueService`（services）、`EventBus`（services）
- **配置**：`frontmatter` 解析库（轻量 YAML parser，如 gray-matter 或手动解析）
- **参考**：openclaw 的 skill 加载模式（frontmatter 解析、isolated agent run）
