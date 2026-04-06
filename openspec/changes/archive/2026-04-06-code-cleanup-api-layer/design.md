## Context

当前 API 层（`api/issues.ts`、`api/projects.ts`）直接调用 StateManager 的 CRUD 方法，绕过了已有的 Service 层（`IssueService`、`ProjectService`）。这是代码在不同时间点添加的演化遗留——早期 endpoint 用 Service，后期 endpoint 直接用 StateManager。

同时 CLI 层有三个完全相同的 `apiClient()` 函数分布在 `issue.ts`、`quick.ts`、`project.ts`。

StateManager 当前同时承担两个职责：repo 工厂/DI 容器（`getIssueRepo()`、`getConfigRepo()` 等）和薄 CRUD 封装（`createIssue()`、`updateIssueStage()` 等），后者与 Service 层完全重叠。

## Goals / Non-Goals

**Goals:**

- API 层统一通过 Service 层操作，消除 StateManager 与 Service 的调用混乱
- CLI 层消除 `apiClient()` 三份重复代码
- StateManager 瘦身为 repo 工厂 + config 管理，不再暴露与 Service 重叠的 CRUD

**Non-Goals:**

- 不给 Service 层增加业务规则校验（如 stage transition 合法性）——留给后续 change
- 不重构 API handler 的编排逻辑（start/approve 端点中的 worktree/agent 逻辑仍留在 handler）
- 不创建 LabelService（`labels.ts` 逻辑简单，但需要从 StateManager 迁移到 Service 调用）
- status.ts 和 labels.ts 需要迁移，它们目前依赖将被移除的 StateManager 方法

## Decisions

### D1: StateManager 保留 repo getter，移除重叠 CRUD

StateManager 保留 `getIssueRepo()`、`getCommentRepo()` 等 getter（AgentRunner 等需要直接拿 repo），但移除与 Service 重叠的方法：`createIssue`、`getIssueByNumber`、`updateIssueStage`、`updateIssueStatus`、`getCommentsByIssue`、`createComment`、`getLabels`、`loadProjects`、`loadIssues`、`getProjectById`、`getProjectByName`、`saveProject`、`deleteProject`、`setCurrentProjectId`、`getCurrentProjectId`。

config 管理（`getCurrentProjectId`/`setCurrentProjectId`）移入 ProjectService（已有 `getCurrent()`/`setCurrent()`）。

**替代方案**：完全删除 StateManager，让所有消费者直接接收 repo 实例。不采用——改动面太大，且 StateManager 作为 DI 根节点在 server/index.ts 中有清晰的组织作用。

### D2: API 层接收 Service 实例而非 StateManager

`createIssueRoutes(stateManager)` 改为 `createIssueRoutes(issueService, projectService, ...)`。同样 `createProjectRoutes` 改为接收 ProjectService。

server/index.ts 负责组装所有 Service 并注入到 API 路由。

**替代方案**：StateManager 变成 Service 容器（`stateManager.getIssueService()`）。不采用——增加了一层间接，且 StateManager 的职责应该更轻。

### D3: IssueService 补齐缺失方法

当前 IssueService 缺少：`createComment`、`getCommentsByIssue`、`update`（通用更新，支持 title/body/labels）。这些从 API 层迁移下来。

`create` 方法增加 `labels` 参数支持（当前只支持 `title` + `body`）。

### D4: API handler 编排逻辑暂不下沉

`api/issues.ts` 中 start/approve 端点的编排逻辑（校验 → 改 stage → 建 worktree → 启动 agent）仍留在 handler。这些逻辑涉及多个 Service（IssueService + WorktreeManager + AgentRunnerService），强行下沉需要一个协调 Service，当前阶段不值得。

仅将纯数据操作（CRUD、stage/status 变更）走 Service，编排留在 handler。

### D5: apiClient 提取为 `cli/api-client.ts`

提取到 `packages/cli/src/cli/api-client.ts`，导出 `apiClient` 函数和 `API_BASE` 常量。三个文件改为 import。

### D6: ProjectService 提供 getCurrentId() 方法

ProjectService 已有 `getCurrent()` 返回 `Project | null`，但 API 层多处需要 `string | null`（project ID）。StateManager 的 `getCurrentProjectId()` 直接返回 config 中的 ID 字符串。

在 ProjectService 添加 `getCurrentId(): string | null` 方法，直接从 config 读取 currentProjectId，避免 API 层需要先 getCurrent() 再访问 .id。

### D7: status.ts 和 labels.ts 必须迁移

这两个文件目前依赖将被移除的 StateManager 方法：
- status.ts: `loadProjects`, `loadIssues`, `getProjectById`, `getCurrentProjectId`
- labels.ts: `getCurrentProjectId`, `getLabels`

T-006 强制要求完成它们的迁移：status.ts 接收 ProjectService + IssueService，labels.ts 接收 ProjectService（getCurrentId）或直接 repo 访问（getLabels）。

## Risks / Trade-offs

- **[Risk] StateManager 移除方法可能遗漏消费者** → 用 TypeScript 编译检查：移除方法后编译失败的地方就是需要改的消费者
- **[Risk] ProjectService.delete 不级联删除 issues** → 当前 StateManager.deleteProject 有 `issueRepo.deleteByProjectCascade`，迁移时确保 ProjectService.delete 注入 IssueRepo 并包含级联删除
- **[Risk] Service 构造函数签名变更** → IssueService 需注入 CommentRepo，ProjectService 需注入 IssueRepo，server/index.ts 中创建实例的地方需要更新
- **[Risk] status.ts/labels.ts 迁移遗漏** → T-006 强制要求完成，确保在 T-008 移除 StateManager 方法前全部迁移完毕
