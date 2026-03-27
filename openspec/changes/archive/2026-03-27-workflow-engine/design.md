## Context

mohist 的工作流组件已基本就绪：状态机（issue-workflow）、阶段处理器（stage-handlers）、Agent 运行器（agent-runner）、任务仓库（task-repo）。但缺少核心的 WorkflowEngine 来驱动整个流程。当前任务入队后无人消费，Agent 不会被 spawn。

同时，多个 Issue 并行处理时缺少 Git 隔离机制。所有 Agent 在同一个工作目录中运行，可能导致文件冲突和互相覆盖。

现有架构：
- Server（HTTP）→ API Routes → WorkflowService（状态转换）→ TaskQueue（内存队列）
- AgentRunner spawn `opencode agent` 子进程，但未被任何调度器调用
- TaskRepo 已支持持久化，但 TaskQueue 是独立的内存实现

## Goals / Non-Goals

**Goals:**
- 实现多 Worker 并行执行引擎，让 Issue 工作流真正运转起来
- 基于 git worktree 实现每个 Issue 的隔离工作区
- 支持本地合并流程（CLI 端 git merge + worktree 清理）
- 替换内存 TaskQueue 为 TaskRepo 持久化队列
- Agent 日志按 Issue 隔离存储

**Non-Goals:**
- 不实现 Explore/Refinement 阶段（后续迭代）
- 不实现 GitHub PR 创建和合并（后续迭代）
- 不实现实时用户干预（追加指令到运行中的 Agent）
- 不实现优先级队列
- 不实现任务自动重试

## Decisions

### D1: 多 Worker 模式而非 Actor 框架

**选择**: N 个独立 Worker 循环，每个 Worker 从 TaskRepo 取任务并执行。

**替代方案**:
- Actor 系统（如 BullMQ、自定义 Actor 框架）：对单机 CLI 工具过重
- Promise 池（单循环管理多个 Promise）：控制流复杂

**理由**: 多 Worker 最直观，每个 Worker 就像一个独立线程，`await` 只阻塞自身。8 个 Worker 对应 `maxConcurrentAgents` 配置。

### D2: 基于 TaskRepo 而非内存队列

**选择**: 删除内存 TaskQueue，WorkflowEngine 直接读写 TaskRepo。

**理由**: 
- 当前内存 TaskQueue 和 TaskRepo 数据不同步，服务器重启后状态丢失
- TaskRepo 已有 `findPending()`、`findRunning()`、`updateStatus()` 等方法
- SQLite 在单进程场景下性能完全足够
- `recoverState()` 已将 running 任务标记为 failed，重启后无需额外恢复逻辑

### D3: git worktree 而非 git branch

**选择**: 每个 Issue 创建独立的 git worktree。

**替代方案**:
- git branch + 切换目录：不能并行，多个 Agent 无法同时工作
- Docker 容器：过重，需要 Docker 守护进程

**理由**: worktree 提供真正的目录隔离，多个 Agent 可以同时在不同目录中工作，互不干扰。.git 内部文件共享，磁盘开销可控。

### D4: Worktree 在 start 时创建

**选择**: `mo issue start` 时立即创建 worktree 和分支。

**替代方案**:
- Implementing 时才创建：设计产出和代码产出在不同位置，审查不便

**理由**: 统一在 worktree 中工作，简单一致。design.md 和代码变更都在同一个分支上，方便审查。

### D5: 本地合并由 CLI 执行

**选择**: `mo issue approve`（第二次，从 waiting-review 到 done）时，CLI 在主工作区执行 git merge。

**替代方案**:
- Server 端执行：Server 进程需要知道主项目路径，且可能没有 git 权限

**理由**: approve 是用户主动操作，CLI 直接在当前目录执行 git 命令最简单。Server 只负责状态转换，不执行 git 操作。

### D6: 同一 Issue 同时只允许一个 Task 执行

**选择**: 同一 Issue 同时只允许一个 Task 执行，通过 D12 的 SQL `NOT EXISTS` 约束在原子层面保证。

**理由**: 防止同一 Issue 的不同阶段并行（如 Designing 和 Implementing 同时执行导致状态混乱）。

### D7: Worktree 路径通过内存 Registry 传递

**选择**: WorkflowEngine 维护一个 `Map<issueId, worktreePath>` 内存映射，Server 启动时从 `git worktree list` 恢复。

**替代方案**:
- Task 表加 worktree_path 字段：改数据库 schema，增加迁移复杂度
- 每次执行时查询文件系统：性能差且不可靠

**理由**: 内存映射最简单。Server 启动时通过 `git worktree list` 扫描已有 worktree 即可恢复映射。不需要持久化——worktree 本身就在磁盘上，映射丢失后重建即可。

**StageHandler 接口变更**: `StageHandler.execute(issue, task)` 签名扩展为 `execute(issue, task, context)`，其中 `context` 包含 `worktreePath: string` 和 `projectName: string`。Engine 从 worktree registry 获取 worktreePath，从 Issue 的 Project 获取 projectName，构造 context 传给 Handler。Handler 再传给 AgentRunner 的 `spawnAgent`。这样 Engine 是唯一的路径组装点，StageHandler 和 AgentRunner 只消费路径。

### D8: approve（waiting-review → done）由 CLI 本地执行

**选择**: `mo issue approve` 当 Issue 处于 waiting-review 时，CLI 先本地执行 git merge，成功后再调 Server API 做状态转换。

**替代方案**:
- 先调 Server API 做状态转换，再本地 merge：merge 失败但状态已变，无法回退
- 全部在 Server 端执行：Server 需要操作 git，职责不清

**理由**: 本地 merge 优先保证安全——merge 失败不改状态。diff 和 logs 也是纯本地命令，不需要 Server 参与。只有需要状态转换时才调 Server API。

### D9: Agent 失败不自动重试

**选择**: Agent 失败后标记 Issue 为 blocked，等待用户介入。

**替代方案**:
- 自动重试 N 次：Agent 失败通常是 prompt 或任务问题，重试浪费 token
- 指数退避重试：增加复杂度，效果不确定

**理由**: 用户可以查看日志了解失败原因，修改后手动重试。

### D10: Task 接口只保留 issueId，删除 issueNumber

**选择**: Task 接口从 `issueNumber: number` 改为 `issueId: string`（UUID）。

**替代方案**:
- 同时保留 issueId + issueNumber：需要 JOIN issues 表，增加查询复杂度

**理由**: 数据库 tasks 表存的是 `issue_id TEXT`（UUID），不是 issue number。当前 `rowToTask()` 硬编码 `issueNumber: 0` 就是这个不匹配造成的。所有消费 Task 的地方（StageHandlers、WorkflowEngine）都已经拿到完整 Issue 对象，不需要从 Task 上取 number。删除 issueNumber 消除了歧义，简化了代码。

**影响**: `TaskQueue.enqueue(issueNumber, ...)` 签名需要改为 `enqueue(issueId, ...)`，但由于 T-002 要删除 TaskQueue，所以只需更新 `Task` 接口和 `rowToTask()`。

### D11: Git 操作职责划分 — Server 创建，CLI 合并+清理

**选择**: 明确划分 git 操作职责：

| 操作 | 执行者 | 原因 |
|------|--------|------|
| 创建 worktree | Server（`POST /issues/:number/start`） | Server 有 `Project.path`，且需要注册到 Engine 的 worktree registry |
| git merge | CLI（`mo issue approve`） | 用户主动操作，CLI 直接在本地执行最简单（D5） |
| 清理 worktree | Server（`POST /issues/:number/cleanup`） | Server 持有 WorktreeManager 实例和 worktree registry |
| git diff | CLI（`mo issue diff`） | 纯只读操作，CLI 通过 API 获取 `projectPath` 后本地执行 |

**CLI 获取 project path**: `GET /api/issues/:number` 响应中包含 `projectPath` 字段（来自 `Project.path`）。CLI 使用该路径执行 git merge 和 git diff。

**理由**: 
- worktree 创建时 Server 需要注册到内存 registry，所以必须 Server 做
- cleanup 同理需要更新 registry
- merge 和 diff 是用户主动触发的本地操作，CLI 直接执行更简单
- CLI 不需要本地维护 Project 信息，通过 API 获取即可

### D12: findAndClaim 原子查询包含 same-Issue 约束

**选择**: 在 findAndClaim 的 SQL 查询中直接包含 `NOT EXISTS` 子查询，确保同一 Issue 已有 running Task 时不会选中其 pending Task。

**替代方案**:
- 先 findAndClaim 再检查：Task 已被标记为 running，无法"放回去"，会导致死锁

**理由**: 原子操作必须在 SQL 层面保证一致性。两步操作（先 claim 再 check）在并发场景下会导致 Task 被 claim 后因 same-Issue 约束被跳过，但状态已经是 running，永远无法被其他 Worker 处理。

**SQL 示例**:
```sql
UPDATE tasks SET status = 'running', started_at = ?
WHERE id = (
  SELECT id FROM tasks t
  WHERE t.status = 'pending'
  AND NOT EXISTS (
    SELECT 1 FROM tasks t2
    WHERE t2.issue_id = t.issue_id AND t2.status = 'running'
  )
  ORDER BY t.started_at ASC
  LIMIT 1
)
RETURNING id, issue_id, project_id, stage, started_at;
```

### D13: pause 时通过 API 通知 Engine 终止 Agent

**选择**: pause API 路由在标记 Issue 为 paused 后，调用 `WorkflowEngine.killAgentByIssueId()` 终止该 Issue 的运行中 Agent。Engine 内部将该 Issue 的 running Task 标记为 failed（reason: "user_paused"），然后 kill Agent 进程。

**理由**: 仅改状态不杀 Agent 会导致 Agent 继续运行，完成后可能推进已暂停的 Issue 阶段。

**reverse lookup 机制**: AgentRunner 的 `processes` Map 以 `taskId` 为 key，但 `killAgentByIssueId(issueId)` 需要通过 issueId 找到对应的 running task 和 Agent 进程。Engine 维护一个 `Map<issueId, taskId>` 映射，Worker 取到 task 时注册，task 完成/失败时清除。`killAgentByIssueId` 流程：

```
killAgentByIssueId(issueId):
  1. 从 issueTaskMap 获取 taskId
  2. TaskRepo.updateStatus(taskId, 'failed', 'user_paused')
  3. AgentRunner.killAgent(taskId)
  4. 从 issueTaskMap 清除
```

同时 TaskRepo 需要新增 `findRunningByIssue(issueId)` 方法（当前只有 `findRunning()` 和 `findByIssueId()`），用于 pause 路由的前置检查和 Engine 内部验证。

**pause 时的 Task 处理**: Task 标记为 failed 但 Issue 保持 paused（不标 blocked）。这样 resume 时状态干净：paused → active → 创建新 Task。Worker 的 Promise reject 后发现 Task 已经是 failed，幂等跳过，不会重复标记 Issue 为 blocked。

### D14: 基于 project 隔离的文件系统路径

**选择**: worktree 和日志目录按项目隔离：

```
~/.mohist/
├── mohist.db
└── projects/
    └── {projectName}/
        ├── worktrees/
        │   ├── issue-1/
        │   └── issue-2/
        └── logs/
            ├── issue-1/
            │   ├── agent-designing.log
            │   └── agent-implementing.log
            └── issue-2/
```

**替代方案**:
- `~/.mohist/worktrees/issue-{N}/`：多项目冲突（项目 A 和 B 都有 issue #1）

**理由**: 项目名在数据库中是 UNIQUE 的，可以作为目录名。opencode agent 在 worktree 目录下独立运行，worktree 路径只需传给 AgentRunner 作为 cwd。日志按项目分组也方便管理和清理。

**projectName sanitize**: 项目名作为目录名需要处理特殊字符（空格、`/`、`..`等）。使用简单的 slug 函数（小写、空格转 `-`、去除特殊字符）即可。

### D15: resume 统一处理所有"卡住"场景

**选择**: `mo issue resume` 统一覆盖三种"卡住"场景，自动创建新 Task：

| 场景 | Issue 状态 | resume 行为 |
|------|-----------|------------|
| pause 后恢复 | paused | → active + 创建新 Task（当前 stage） |
| Agent 失败后重试 | blocked + Agent 阶段 | → active + 创建新 Task（当前 stage） |
| Server 重启后恢复 | active + Agent 阶段 + 无 pending Task | → 创建新 Task（当前 stage） |

**resume 前置条件**:
- Issue 必须在 Agent 阶段（designing 或 implementing）
- Issue 不能有 running 或 pending 的 Task
- Issue 不能在 draft、waiting-*、done 阶段

**理由**: 对用户来说，pause 恢复、失败重试、重启恢复的本质是一样的——"继续处理这个 Issue"。一个命令覆盖所有场景，用户不需要理解内部区别。Server 重启后 Issue 一定是 active + 无 pending Task（因为 recoverState() 标记 running 为 failed），用户只需 `mo issue resume` 即可恢复。

**Agent 在已有 worktree 中重跑**: resume 创建的新 Task 使用 Issue 的当前 stage。Agent 在同一个 worktree 里运行，会看到已有内容（半写完的 design.md 或代码）并继续/修正。

## Risks / Trade-offs

**[磁盘占用]** → 每个 worktree 是完整的工作目录副本。缓解：用完立即清理，可配置最大 worktree 数量，node_modules 可用符号链接。

**[合并冲突]** → 主分支在 Agent 工作期间有新提交时，merge 可能冲突。缓解：merge 失败时标记 Issue 为 blocked，提示用户手动解决。

**[git worktree 前置条件]** → 项目必须是 git 仓库。缓解：`mo project init` 时检查，非 git 仓库给出明确提示。

**[并发控制粒度]** → 基于 SQLite 的 TaskRepo 在高并发下可能有锁竞争。缓解：当前 maxConcurrentAgents=8，SQLite 完全够用。使用 WAL 模式减少锁等待。

**[服务器停止时的任务丢失]** → 优雅停止有超时，超时后强制终止。缓解：TaskRepo 已持久化任务状态，重启后 running 任务被标记为 failed，用户可手动重试。

**[多 Worker 竞态取任务]** → 多个 Worker 同时调用 findAndClaim() 可能取到同一个 Task。缓解：使用 findAndClaim 原子操作，SQL 中包含 `NOT EXISTS` 子查询确保同一 Issue 只有一个 running Task（见 D12）。

**[Worktree 路径传递]** → WorkflowEngine 需要知道 Task 对应的 worktree 路径。缓解：通过内存 Registry（Map<issueId, worktreePath>）维护，启动时从 `git worktree list` 恢复。

**[approve 的 CLI/Server 职责]** → waiting-review → done 需要 CLI 做 git merge 但状态在 Server。缓解：CLI 先本地 merge，成功后再调 Server API 做状态转换。cleanup 通过专用 API `POST /issues/:number/cleanup` 让 Server 执行（见 D11）。

**[CLI 缺少 project 路径]** → CLI 是纯 HTTP 客户端，不知道项目文件路径，无法执行 git 操作。缓解：`GET /api/issues/:number` 返回中包含 `projectPath` 字段，CLI 使用该路径执行 git merge 和 diff（见 D11）。

**[pause 不终止 Agent]** → 仅标记 Issue 为 paused 不杀 Agent，Agent 完成后可能推进已暂停 Issue。缓解：pause API 调用 Engine 的 `killAgentByIssueId()` 终止对应 Agent（见 D13）。

**[Task 完成时 Issue 已被暂停]** → Agent 运行期间用户暂停了 Issue，Agent 完成后 Engine 不应推进阶段。缓解：Engine 在 Task 完成后检查 Issue 状态，如果 paused 则只标记 Task completed，不推进阶段。

**[Server 重启后 Issue 卡住]** → Agent 运行期间 Server 重启，running Task 被 recoverState() 标记为 failed，但 Issue 仍在 Agent 阶段且无 pending Task。缓解：用户执行 `mo issue resume` 创建新 Task 恢复（见 D15）。

**[Agent 失败后无法重试]** → Issue 被标记为 blocked 后没有命令可以恢复。缓解：`mo issue resume` 也覆盖 blocked 状态的恢复（见 D15）。

**[pause 时 Task 和 Issue 状态竞态]** → Agent 被 kill 后 Worker 走失败路径可能把 Issue 标记为 blocked，覆盖 paused 状态。缓解：pause 时 Engine 先将 Task 标记为 failed 再 kill Agent，Worker 的 reject 为幂等操作（见 D13）。

**[多项目 worktree 路径冲突]** → 不同项目的相同编号 Issue 争抢同一个 worktree 目录。缓解：worktree 路径按项目隔离 `~/.mohist/projects/{name}/worktrees/`（见 D14）。

**[projectName 目录特殊字符]** → 项目名含空格或特殊字符导致目录创建失败。缓解：使用 slug 函数处理项目名。
