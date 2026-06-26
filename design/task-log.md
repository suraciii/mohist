---
purpose: "Task 执行日志的设计：采集管道、上报通道、存储与查询归属。"
include:
  - "TaskLog 的概念模型与归属边界（不进 WorkflowRun）。"
  - "Runner 侧采集管道与上报通道。"
  - "Server 侧存储与查询。"
  - "与现有执行痕迹概念（AgentSession transcript、Artifact）的关系。"
  - "工业参考（GitHub Actions Runner / Azure Pipelines Agent）的关键设计点。"
exclude:
  - "全局控制平面/执行平面边界；见 architecture.md。"
  - "Runner 聚合信息结构与 status 快照；见 runner.md。"
  - "task dispatch / report / claim 链路；见 workflow/scheduling.md。"
  - "AgentSession transcript 的内部模型；已有实现。"
  - "Web UI 组件细节；见 web-ui.md。"
style:
  - "日志与状态是分离的两个对象，仅通过 workId 关联。"
  - "日志走独立上报通道，不进 WorkResult。"
  - "采集经过单一汇聚点，不各处拼 combinedOutput。"
  - "Prefer code blocks over prose."
---

# Task 执行日志

task 执行过程日志的设计。对标 GitHub Actions / Azure Pipeline 的 step log——可折叠、逐行、可实时流式的执行过程记录。

## 问题

task 执行中运维动作的 stdout/stderr 被丢弃：

```
Runner (WorkExecutor.execute)
  ├─ prepareWorkspace   (git clone/checkout)     ← 输出丢弃
  ├─ checkBranchStability (git rev-parse)        ← 输出丢弃
  ├─ action() (git rebase/push/openspec/...)
  │      └─ runCommand/git() → combinedOutput    ← 聚合成摘要，原始行丢弃
  ├─ enforceCleanWorktree (git status/diff)      ← 输出丢弃
  └─ captureArtifacts  → artifactUploadIds       ← 已持久化 ✓
```

用户只能看到 task 的终态结果（status + message + output JSON），看不到执行过程。失败时无法像 GA 那样"翻日志定位"。

### 已有但不覆盖此问题的基础设施

| 概念 | 覆盖 | 不覆盖 |
|------|------|--------|
| AgentSession transcript | agent 对话流（message/tool/reasoning）✓ | ops 命令过程（git/shell） |
| Artifact | 产出文件 ✓ | 执行过程 |
| `~/.mohist/logs/*.log` | server daemon 自身日志 | task 执行日志（无关） |
| task output JSON | 结构化结果（errorCode/verdict/SHAs） | 逐行过程 |

## 归属边界（核心）

**TaskLog 属于 Runner 子域的执行痕迹，不进 WorkflowRun。**

```
              不经过                     经过
                                         │
POST /api/runner/{runnerId}/work/{workId}/task-log    POST /api/runner/{runnerId}/report
  → 写入独立 TaskLogStore (Runner 子域)               → WorkResult → WorkflowGrain 裁定
  → WorkflowRun 永不感知                               → WorkResult 永不携带日志
```

判据：

| 检验 | TaskLog | 结论 |
|------|---------|------|
| 是 WorkflowRun 状态裁定的输入？ | 否 | 不进 WorkResult |
| WorkflowRun 行为签名需要它？ | 否 | 不进 WorkflowRun 聚合 |
| 是审查证据（类比 Artifact）？ | 是 | 独立持久化，通过 workId 关联 |

这与 AgentSession transcript 同构——独立 grain/store，通过 label（workflowRunId+sessionName）关联 workflow run，但不被 WorkflowRun 管理。

## 概念模型

```
TaskRun (1)                                  ← 已有，在 WorkflowRun 内
 ├─ status / message / output               ← 已有：终态结果（状态裁定输入）
 ├─ Artifacts                               ← 已有：产出物
 ├─ AgentSession transcript                 ← 已有：agent 对话（仅 agent task）
 └─ TaskLog (1)                             ← 新增：执行过程日志
      └─ LogEntry[]
           ├  seq            # 单调递增行号
           ├  timestamp
           ├  stream         # stdout | stderr | system
           ├  source         # workspace-prep | branch-check | action:rebase | cleanup | ...
           └  text
```

TaskLog 与 TaskRun 是 1:1，但**存储和生命周期完全独立**——TaskLog 是 Runner 产生的痕迹事实，TaskRun 是 Workflow 裁定的状态。两者仅通过 `workflowRunId + workId` 关联。

## 采集管道（Runner 侧）

### 单一汇聚点

所有输出经过 `ActionContext.log`，不各处拼 `combinedOutput`：

```ts
interface TaskLogger {
  write(source: string, stream: "stdout" | "stderr" | "system", text: string): void
}

// 注入到 ActionContext
interface ActionContext {
  // ... 现有字段
  log: TaskLogger
}
```

executor 的每个执行阶段向它写：

```text
prepareWorkspace     → log.write("workspace-prep", "system", "cloning ...")
                       log.write("workspace-prep", "stdout", gitOutput)
checkBranchStability → log.write("branch-check", "system", "start boundary ...")
action() 内的 git()/runCommand() → 逐行转发 stdout/stderr
enforceCleanWorktree → log.write("cleanup", "system", "worktree dirty, attempt 1")
ACP agent            → 不重复 transcript，仅记 system 里程碑行
```

### 升级 runCommand / git() 为逐行输出

当前 `system/process.ts` 的 `runCommand` 只返回聚合 stdout/stderr。改为可选 `onLine` 回调逐行输出（参考 GA/Azure 的 `ProcessInvoker.OutputDataReceived`）：

```ts
interface RunCommandOptions {
  // ... 现有
  onStdout?: (line: string) => void
  onStderr?: (line: string) => void
}
```

executor 调用时把回调接到 `ActionContext.log.write(source, stream, line)`。这是收益最大、改动最小的一步——覆盖 rebase/push/openspec/health-check 等绝大多数 ops 输出。

### TaskLogCollector

每个 work item 一个 collector，缓冲日志行：

```text
TaskLogCollector (per work)
  ├─ entries: LogEntry[]          # 带 seq 行号
  ├─ flush()                      # 批量 POST（攒一批再发，不每行一请求）
  └─ capacity limit               # 超限截断，尾部保留错误上下文
```

## 上报通道（独立，不走 report）

```text
Runner 执行 work:
  采集 ops 输出 → TaskLogCollector
  ──(执行中/完成后)──▶ POST /api/runner/{runnerId}/work/{workId}/task-log
                        独立端点，写独立 store
                        WorkResult 不变，report 不携带日志
  ──(终态)──────────▶ POST /api/runner/{runnerId}/report
                        照旧进 WorkflowGrain 裁定
```

时序对标 artifact：artifact 先 upload 拿 id 再 report 带 id；task-log 先落独立存储，report 完全不感知它。区别是 log 不需要被 report 回引（审查证据，非裁定输入）。

### 两期模式

| 阶段 | 模式 | 说明 |
|------|------|------|
| Phase 1 | 终态批量 | task-log 在 report 之前一次性 POST，task 完成即可看完整日志 |
| Phase 2 | 实时流式 | 执行中分批 flush + SignalR `taskLog.delta` 推送，边执行边看 |

实时流式复用 `workflowAgentSessionRuntimeEvents` 同款通道模式（runner→server 批量 + SignalR fan-out）。

## 存储与查询（Server 侧）

### 模块放置

```text
packages/server/src/Mohist.Server/
  Runner/                          ← TaskLog 归此（执行痕迹）
    ...TaskLogStore.cs             ← 独立持久化
  Workflow/                        ← 不动，WorkflowRun 永不感知 task log
```

### 存储结构

参照 `AgentSessionRuntimeEvents` 的范式：

```text
TaskLogEntries
 ├ Id (PK)
 ├ WorkflowRunId (索引)
 ├ WorkId (索引)
 ├ Seq                # 该 task 内单调递增
 ├ Timestamp
 ├ Source             # workspace-prep / action:rebase / cleanup / ...
 ├ Stream             # system / stdout / stderr
 └ Text               # nvarchar max
```

写入由 task-log 端点 handler 直接落库，不经过 `RunnerGrain.ReportWorkflowResultAsync`，不转发给 `WorkflowGrain`。

### 查询 API

```text
GET /api/workflow-runs/{runId}/work/{workId}/task-log?cursor=&limit=
  → 游标分页（类比 LogsRoutes 的 tail 模式）
```

路径挂在 workflow-run 下仅为查询便利（类比 `/api/workflow-runs/{runId}/sessions`），语义是"按 run+work 查执行痕迹"，不是 WorkflowRun 的管理属性。

### 容量控制

- 单 task log 上限（如 256KB / 5000 行），超限截断标记 `truncated`。
- 失败 task 优先保留尾部（错误上下文）。

## 与现有概念的关系

| 概念 | 回答的问题 | 归属 | TaskLog 关系 |
|------|-----------|------|-------------|
| TaskLog（新） | task 执行**过程**发生什么 | Runner 子域 | — |
| AgentSession transcript | agent **对话**了什么 | Agent 子域 | 互补：agent task = transcript + log |
| Artifact | task **产出**什么文件 | 审查证据 | 同级证据 |
| task output JSON | task 的**结构化结果** | Workflow 裁定输入 | 互补：log 给过程，output 给结论 |
| `~/.mohist/logs` | **server daemon** 自身日志 | 基础设施 | 无关 |

四者（log/transcript/artifact/output）互补不重叠。

## 工业参考

GitHub Actions Runner 与 Azure Pipelines Agent 同源，日志架构一致。关键设计点：

### 1. 状态与日志是分离的两个对象

Timeline（状态）和 TaskLog（日志）独立存储，仅通过 record.Id 关联。对应 Mohist：TaskLog 与 TaskRun/WorkResult 分离。

### 2. 单一汇聚点 Write(tag, line)

`ExecutionContext.Write`（azure-pipelines-agent/.../ExecutionContext.cs:757）是所有输出的漏斗，普通输出、error、warning 都经过它，由它分发到持久化 + 实时两条通道。对应 Mohist 的 `ActionContext.log`。

### 3. stdout/stderr 逐行捕获

`ProcessInvoker`（手动读流，不用 .NET DataReceivedEvent 避免性能问题）逐行触发 `OutputDataReceived`/`ErrorDataReceived` 事件。对应 Mohist 的 `runCommand.onLine`。

### 4. 三条独立通道

GA/Azure 的 `JobServerQueue` 维护三条独立队列：

| 通道 | 方法 | Mohist 对应 |
|------|------|------------|
| 状态更新 | `QueueTimelineRecordUpdate` | `report()` → `WorkResult`（已有） |
| 持久化日志 | `QueueFileUpload(type=Log)` 分页 8MB | `TaskLogStore`（简化：不分页，单机） |
| 实时 feed | `QueueWebConsoleLine` → `AppendTimelineRecordFeedAsync` | `task-log` 端点 + SignalR（Phase 2） |

三条互不阻塞，各有独立批处理。Mohist 单机本地优先，简化分页/并行上传，但保留"批量 flush"模式。

### 5. 行号 + secret masking

GA 记录 `totalLines`，issue 带 `logFileLineNumber` 可跳转日志行。Mohist 的 TaskLog 记 `seq` 行号。

## 分期落地

| 阶段 | 范围 | 收益 | 改动面 |
|------|------|------|--------|
| Phase 1 终态日志 | runCommand/git() 加 onLine → ActionContext.log → 批量 POST task-log → 存表 → Web task 展开显示 | 失败可定位过程 | executor + runCommand + 1 张表 + 1 个端点 + Web 日志面板 |
| Phase 2 实时流式 | 执行中分批 flush → SignalR 推 taskLog.delta → Web 自动追加 | 执行中可见 | 新增流式通道（复用 runtime-events 模式） |
| Phase 3 体验增强 | 搜索、下载、级别过滤、agent task 里程碑行 | 对齐 GA 完整体验 | 纯前端 |

Phase 1 性价比最高：改动集中在 runner 命令执行包装 + 独立端点，立刻让所有 ops task 失败可定位，不触碰 workflow 状态机。

## 范围外

- 不改 report / WorkResult 结构（日志走独立通道）。
- 不改 WorkflowRun 聚合根（永不感知 task log）。
- 不替代 AgentSession transcript（agent 对话痕迹已有独立链路）。
- 不做分布式日志聚合 / 多 runner 日志归档（当前单机假设）。
- 不改全局控制平面/执行平面边界（见 architecture.md）。
