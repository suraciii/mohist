---
purpose: "Task 执行日志的设计：采集管道、上报通道、存储与查询归属。"
include:
  - "TaskLog 的概念模型与归属边界（不进 WorkflowRun）。"
  - "Runner 侧采集管道与上报通道。"
  - "Server 侧存储与查询。"
  - "与现有执行痕迹概念（AgentSession transcript、Artifact）的关系。"
  - "工业参考（以 GitHub Actions Runner 为榜样）。"
exclude:
  - "全局控制平面/执行平面边界；见 architecture.md。"
  - "Runner 聚合信息结构与 status 快照；见 runner.md。"
  - "task dispatch / report / claim 链路；见 workflow/scheduling.md。"
  - "AgentSession transcript 的内部模型；已有实现。"
  - "Web UI 组件细节；见 web-ui.md。"
style:
  - "以 GitHub Actions Runner 为榜样：单一汇聚点 Write、状态/日志分离、独立通道。"
  - "日志与状态是分离的两个对象，仅通过 workId 关联。"
  - "日志走独立上报通道，不进 WorkResult。"
  - "采集经过单一汇聚点，不各处拼 combinedOutput。"
  - "Prefer code blocks over prose."
---

# Task 执行日志

task 执行过程日志的设计。**以 GitHub Actions Runner 的 step log 为榜样**——可折叠、逐行、可实时流式的执行过程记录。

GA Runner 是 Azure Pipelines Agent 的 fork，两者日志骨架一致（单一汇聚点 `ExecutionContext.Write` + `ProcessInvoker` 逐行捕获 + `JobServerQueue` 多通道）。GA fork 后做了若干演进（文件命令、WebSocket 推流、砍掉 stdout/stderr 区分），本文以 GA 为参照。GA 源码路径（仓库 `actions/runner`）：`src/Runner.Worker/ExecutionContext.cs`、`src/Runner.Common/JobServerQueue.cs`、`src/Runner.Common/Logging.cs`、`src/Runner.Sdk/ProcessInvoker.cs`。

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
              不经过                              经过
                                                 │
POST /api/{ownerKind}/{ownerId}/work/{workId}/task-log    POST /api/runner/{runnerId}/report
  → 写入独立 TaskLogStore (Runner 子域)                    → WorkResult → WorkflowGrain 裁定
  → WorkflowRun 永不感知                                    → WorkResult 永不携带日志
```

端点用 **owner 定位**（`ownerKind` = `workflow-runs` | `agent-jobs`，`ownerId` = workflowRunId 或 agentJobId），不用 runnerId。对标 GA：日志上传 `CreateLogAsync(planId,...)` 全程用 plan/owner 定位，runnerId 只用于握手拉取 job（见工业参考 §1）。也对标 Mohist 现有 artifact 上传（`/api/{workflow-runs|agent-jobs}/{ownerId}/work/{workId}/artifact-uploads`）。

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
           ├  seq            # 单调递增行号（GA PagingLogger._totalLines 同款）
           ├  timestamp
           ├  source         # workspace-prep | branch-check | action:rebase | cleanup | ...
           └  text
```

**不区分 stdout/stderr（对齐 GA）。** GA Runner 在 fork 后主动砍掉了 stream 区分——两个 `OutputManager` 调同一个 `Output`，stdout/stderr 合流到同一份日志、共享同一个行号序列（`Runner.Worker/Handlers/OutputManager.cs` 结构上无 stream 字段）。Mohist 沿用此设计：ops 输出的 stdout/stderr 合流，靠 `source` + 文本内容定位，不靠 stream 维度。

TaskLog 与 TaskRun 是 1:1，但**存储和生命周期完全独立**——TaskLog 是 Runner 产生的痕迹事实，TaskRun 是 Workflow 裁定的状态。两者仅通过 `workflowRunId + workId` 关联。

## 采集管道（Runner 侧）

### 单一汇聚点

所有输出经过 `ActionContext.log`，不各处拼 `combinedOutput`。对标 GA 的 `ExecutionContext.Write`（`Runner.Worker/ExecutionContext.cs:1096`）——所有面向用户的文本都过这一个方法，secret 脱敏、行号自增、多通道路由只写一次：

```ts
interface TaskLogger {
  // 注意：无 stream 参数。stdout/stderr 合流（对齐 GA）。
  // 返回该行的 seq 行号（对标 GA Write 返回 totalLines）
  write(source: string, text: string): number
}

// 注入到 ActionContext
interface ActionContext {
  // ... 现有字段
  log: TaskLogger
}
```

executor 的每个执行阶段向它写（去 stream，靠 `source` 区分阶段）：

```text
prepareWorkspace     → log.write("workspace-prep", "cloning ...")
                       log.write("workspace-prep", gitCombinedOutput)
checkBranchStability → log.write("branch-check", "start boundary ...")
action() 内的 git()/runCommand() → 逐行转发输出（stdout/stderr 合流）
enforceCleanWorktree → log.write("cleanup", "worktree dirty, attempt 1")
ACP agent            → 不重复 transcript，仅记里程碑行
```

### Secret 脱敏（入口统一）

对标 GA：`ExecutionContext.Write` 第一步即 `SecretMasker.MaskSecrets($"{tag}{message}")`（`Runner.Worker/ExecutionContext.cs:1098`），**落盘 / 入队 / 推流时数据已是脱敏后**——杜绝"已落盘但还没 mask"的窗口。

`TaskLogger.write` 内部在自增 seq、写缓冲前先脱敏：

```ts
write(source: string, text: string): number {
  const masked = this.maskSecrets(text)        // ① 入口脱敏，对标 GA
  const seq = ++this._seq                       // ② 自增行号
  this._collector.append({ seq, timestamp: this._clock.now(), source, text: masked })
  return seq
}
```

Mohist ops 输出可能含敏感信息：git remote URL 带 credentials、ACP agent API key、issue/token。脱敏在唯一汇聚点做，覆盖所有调用点——只要有一处输出绕过汇聚点就会泄密（GA/Azure 血泪教训）。

masker 的密钥来源（对标 GA `Worker.cs:168` `InitializeSecretMasker`）：runner 配置里的 credentials、ACP agent key、运行时通过 action output 回传的 secret 值。

### 升级 runCommand / git() 为逐行输出

当前 `system/process.ts` 的 `runCommand` 只返回聚合 stdout/stderr。改为可选 `onLine` 回调逐行输出（对标 GA `ProcessInvoker.OutputDataReceived` 事件，`Runner.Sdk/ProcessInvoker.cs:70`）：

```ts
interface RunCommandOptions {
  // ... 现有
  // stdout/stderr 合流到同一回调（对齐 GA：不区分 stream）
  onLine?: (line: string) => void
}
```

GA/Azure 的 `ProcessInvoker` 有个关键设计：**放弃 .NET 自带的 `Process.OutputDataReceived` 事件，自己用独立线程手动读流**。源码注释原文（`Runner.Sdk/ProcessInvoker.cs:20`）："we find a huge perf issue about process STDOUT/STDERR with those events"。改为生产者-消费者模式：读端只入 `ConcurrentQueue` + 打信号，消费端批量 drain 触发回调。

三重防丢行（GA `Runner.Sdk/ProcessInvoker.cs`，Mohist runCommand 升级时须保证）：

| 机制 | GA 位置 | 作用 |
|------|---------|------|
| `EndOfStream` + `ReadLine()` 天然捕获无尾换行的末行 | `:545` | 子进程没输出末尾 `\n` 也不丢行 |
| 进程退出后**无条件再 drain 一次** | `:380` | "防止关闭时还有 pending 输出"——GA 注释原文 |
| 进程退出后读流超时 → 强杀 | `:521` `ProcessExitedHandler` | 防读线程卡死成僵尸 |

executor 调用时把回调接到 `ActionContext.log.write(source, line)`。这是收益最大、改动最小的一步——覆盖 rebase/push/openspec/health-check 等绝大多数 ops 输出。

### TaskLogCollector

每个 work item 一个 collector，缓冲日志行。对标 GA `JobServerQueue` 的批量 drain 模式——采集端只入缓冲队列，flush 端定时/按量批量上报，避免高频 git 输出逐行打 HTTP：

```text
TaskLogCollector (per work)
  ├─ buffer: LogEntry[]          # 带 seq 行号，生产者只 append
  ├─ flush()                     # 批量 POST（攒一批再发，不每行一请求）
  │   ├─ Phase 1：task 结束一次性 flush（终态批量）
  │   └─ Phase 2：执行中定时/按量 flush（实时流式）
  └─ capacity limit              # 超限截断：丢头留尾（见容量控制）
```

生产者-消费者解耦（对标 GA `ProcessInvoker` 的 ConcurrentQueue + 信号 + 批量 drain）：`TaskLogger.write` 只 append 到 buffer，flush 由定时器或 task 结束信号驱动，不阻塞采集端。

## 上报通道（独立，不走 report）

```text
Runner 执行 work:
  采集 ops 输出 → TaskLogCollector
  ──(执行中/完成后)──▶ POST /api/{ownerKind}/{ownerId}/work/{workId}/task-log
                        独立端点，写独立 store
                        WorkResult 不变，report 不携带日志
  ──(终态)──────────▶ POST /api/runner/{runnerId}/report
                        照旧进 WorkflowGrain 裁定
```

ownerKind/ownerId 算法与 artifact 上传一致（`artifact-side-effects.ts:107-112`）：`ownerKind = work.ownerKind === "agent-job" ? "agent-job" : "workflow"`，`ownerId = ownerKind === "agent-job" ? work.agentJobId : work.workflowRunId`。

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
  Infrastructure/Data/Runner/      ← TaskLogStore 归此（ArchitectureRules 强制 store 在 Infrastructure/Data）
    TaskLogEntryRow.cs             ← entity（对标 RunnerWorkRow.cs）
    TaskLogStore.cs                ← store（对标 RunnerWorkStore.cs）
  Api/
    TaskLogRoutes.cs               ← POST + GET 端点（对标 WorkflowArtifactUploadRoutes.cs / LogsRoutes.cs）
  Workflow/                        ← 不动，WorkflowRun 永不感知 task log
```

`ArchitectureRules.cs:120` (`DataStores_AreInInfrastructureData`) 强制所有 `*Store` 类必须在 `Infrastructure.Data` 命名空间；`:176` 强制功能目录（`Runner/`）下只能有 `Domain/Grains/Services`。故 store 不放功能目录 `Runner/`，与现有 `RunnerWorkStore` 同位。

### 存储结构

Phase 1 直接对标 `WorkflowArtifactUploadService` + `WorkflowArtifactPendingUploadRow`——**端点 → store → DbSet.Add → SaveChanges，不经任何 grain**。这比设计早期参照的 `AgentSessionRuntimeEvents`（已删除，见 `20260612123000_DropAgentSessionRuntimeEvents.cs`）简单；该 grain+两阶段 flush 范式是 Phase 2 流式的参考（当前对标 `AgentSessionTranscriptStore` + `TranscriptAccumulator`），Phase 1 不需要。

```text
TaskLogEntries
 ├ Id (PK, 自增 long)
 ├ OwnerKind (索引)        # "workflow" | "agent-job"
 ├ OwnerId (索引)          # workflowRunId 或 agentJobId（随 OwnerKind）
 ├ WorkId (索引)
 ├ Seq                     # 该 task 内单调递增（对标 GA PagingLogger._totalLines）
 ├ Timestamp
 ├ Source                  # workspace-prep / action:rebase / cleanup / ...
 └ Text                    # nvarchar max
```

无 Stream 列（对齐 GA：stdout/stderr 合流）。索引 `(OwnerKind, OwnerId, WorkId, Seq)` 支撑游标分页查询。

写入由 task-log 端点 handler 直接调 `TaskLogStore.AppendAsync` 落库，不经过 `RunnerGrain.ReportWorkflowResultAsync`，不转发给 `WorkflowGrain`。

### 查询 API

```text
GET /api/projects/{projectId}/issues/{number}/workflow/tasks/{taskId}/logs?cursor=&limit=
  → 游标分页（类比 LogsRoutes 的 tail 模式）
  → 返回 { lines: [{seq,timestamp,source,text}], nextCursor, truncated }
  → nextCursor 到末尾为 null
```

走 issue 路径（对齐 artifact 查询 `IssueRoutes.Artifacts.cs`，Web 用 `projectApiPath` 拼接一致）。Web 持有 `issueNumber + taskId`（`StageTaskState.taskId`），不持有 workflowRunId，故不挂 workflow-run 路径。语义是"按 task 查执行痕迹"，不是 WorkflowRun 的管理属性。

### ownerKind 分流

task-log 与 artifact 同构，必须处理 agent-job 不对称：`workflow` 用 `workflowRunId` 作 ownerId，`agent-job` 用 `agentJobId`。POST 端点注册两条路由（`/api/workflow-runs/{ownerId}/...` + `/api/agent-jobs/{ownerId}/...`）。存储层 TaskLogEntryRow 用 OwnerKind + OwnerId 两列区分，不混用 workflowRunId 字段。

### 容量控制

- 单 task log 上限（如 256KB / 5000 行），超限截断标记 `truncated`。
- **丢头留尾**：超限时丢弃头部行，保留尾部错误上下文。对标 GA `JobServerQueue` 实时通道队列满直接丢行（`Runner.Common/JobServerQueue.cs:242`）——实时性 best-effort，错误上下文优先。失败 task 的尾部行（含错误堆栈）是定位关键。
- seq 在截断后保持单调连续（被丢弃的 seq 不重用），保证游标分页稳定。

### seq 的双重用途

seq 不只是排序键，还是**失败定位锚点**（对标 GA 的 `logFileLineNumber`）。GA 的 `AddIssue` 把日志行号存进 `issue.Data["logFileLineNumber"]`（`Runner.Worker/ExecutionContext.cs:856`），让"点错误 annotation → 跳到日志行"成立。Mohist 现阶段用 seq 做游标分页 + 排序；未来若做"失败行快捷跳转"，seq 是现成的锚点（如把 task output 里的错误引用关联到某条 LogEntry 的 seq）。

## 与现有概念的关系

| 概念 | 回答的问题 | 归属 | TaskLog 关系 |
|------|-----------|------|-------------|
| TaskLog（新） | task 执行**过程**发生什么 | Runner 子域 | — |
| AgentSession transcript | agent **对话**了什么 | Session 子域 | 互补：agent task = transcript + log |
| Artifact | task **产出**什么文件 | 审查证据 | 同级证据 |
| task output JSON | task 的**结构化结果** | Workflow 裁定输入 | 互补：log 给过程，output 给结论 |
| `~/.mohist/logs` | **server daemon** 自身日志 | 基础设施 | 无关 |

四者（log/transcript/artifact/output）互补不重叠。

## 工业参考（以 GitHub Actions Runner 为榜样）

GitHub Actions Runner 是 Azure Pipelines Agent 的 fork，日志骨架一致。GA fork 后做了若干演进（双格式命令、文件命令、WebSocket 推流、砍掉 stdout/stderr 区分）。本文以 GA 为主参照，源码引用均带文件路径:行号。

### 1. 状态与日志是分离的两个对象

`TimelineRecord`（状态）和日志内容是两个独立对象，仅通过 `record.Id` 关联。GA 的精妙之处在于关联的建立方式：日志文件上传成功后拿到 `taskLog.Id`，构造一个**只填 `Log` 字段、其余全 null 的 TimelineRecord** 通过 timeline 通道发回服务端，服务端按 `record.Id` 合并（`Runner.Common/JobServerQueue.cs:724`）。客户端 `MergeTimelineRecords` 也用 null-coalescing 做字段级合并（`:598`）。

对应 Mohist：TaskLog 与 TaskRun/WorkResult 分离，走独立端点，通过 `workId` 关联。report 完全不感知日志。

### 2. 单一汇聚点 Write(tag, line)

`ExecutionContext.Write`（`Runner.Worker/ExecutionContext.cs:1096`）是所有面向用户文本的唯一漏斗——stdout、扩展方法 Error/Warning/Output、AddIssue 都汇到它。GA 版相比 Azure 有一关键演进：**`Write` 返回 `long` 行号**（`:1096`），驱动 issue 跳转。Write 内部一次性完成 secret 脱敏 → 行号自增 → 多通道路由。

对应 Mohist 的 `ActionContext.log.write(source, text)`，返回 seq。所有 ops 输出过这一个口，secret 脱敏、seq 自增、缓冲只写一次。

### 3. stdout/stderr 合流（GA 砍掉 stream 区分）

GA fork 后主动砍掉了 stdout/stderr 的 stream 区分——两个 `OutputManager` 调同一个 `Output`（`Runner.Worker/Handlers/OutputManager.cs:77`，全文无 stream/OutputType 字段），stdout/stderr 合流到同一份日志、共享同一个行号序列。网页上仅靠 ANSI 色码渲染差异。

Mohist 沿用此设计：`LogEntry` 无 stream 字段，stdout/stderr 合流。靠 `source`（阶段）+ 文本内容定位错误。

### 4. stdout/stderr 逐行捕获（手动读流，不用框架事件）

`ProcessInvoker`（`Runner.Sdk/ProcessInvoker.cs`）放弃 .NET 自带的 `Process.OutputDataReceived` 事件，自己用独立线程手动读流。源码注释原文（`:20`）："we find a huge perf issue about process STDOUT/STDERR with those events"。

工作方式：生产者-消费者——读端各开一个 `Task.Run`，同步 `ReadLine()` 入 `ConcurrentQueue` + 打信号；消费端批量 drain 触发回调。三重防丢行（`:545`/`:380`/`:521`）：`EndOfStream+ReadLine` 捕获无尾换行末行 / 进程退出后无条件再 drain / 5s 超时强杀。

对应 Mohist 的 `runCommand.onLine`，须保证同样的兜底 drain + 超时强杀。

### 5. 三条独立通道，SLA 分离

GA `JobServerQueue`（`Runner.Common/JobServerQueue.cs`）维护多条独立队列，各由独立 Task 消费，互不阻塞：

| 通道 | 入口 | 消费者 | 批处理 | SLA |
|------|------|--------|--------|-----|
| 实时 feed | `QueueWebConsoleLine` `:200` | `ProcessWebConsoleLinesQueueAsync` `:287` | ≤500行/轮，按 step 切≤100行/批，>1024字符截断 | **best-effort：失败不重试，队列满丢行** |
| 持久化日志 | `QueueFileUpload` `:207` | `ProcessFilesUploadQueueAsync` `:415` | PagingLogger 按 8MB(`Logging.cs:22`) / 2MB block 滚文件 | best-effort（GA 双写 page+block） |
| 状态更新 | `QueueTimelineRecordUpdate` `:227` | `ProcessTimelinesUpdateQueueAsync` `:475` | ≤25 record/轮，`MergeTimelineRecords` 按 Id 合并 | **可靠：失败重试；含 output var 失败→fail job** |

**关键洞察：实时通道可丢、持久通道可靠，SLA 分离。** 实时通道丢的行最终仍出现在持久化日志里，用户不真正丢日志，只是实时控制台可能少几行。GA 持久化日志分页常量：page 8MB / block 2MB（`Runner.Common/Logging.cs:22,28`）。

对应 Mohist：
- 状态更新 → `report()` → `WorkResult`（已有，可靠）
- 持久化日志 → `TaskLogStore`（Phase 1：终态批量，简化不分页，单机）
- 实时 feed → `task-log` 端点 + SignalR（Phase 2，best-effort）

Phase 2 做 SignalR 推送时，**实时推送是 best-effort（可丢、可截断），落库的 TaskLog 才是权威**——实时推送失败不拖累日志完整性，也不需为每行实时推送做重试。

### 6. Secret 脱敏在 Write 入口一次性完成

GA `ExecutionContext.Write` 第一步即 `SecretMasker.MaskSecrets($"{tag}{message}")`（`Runner.Worker/ExecutionContext.cs:1098`），**落盘 / 入队 / 推流时数据已是脱敏后**——杜绝"已落盘但还没 mask"的窗口。masker 还注册 JSON 转义、URL 编码、反斜杠转义三种编码变体，防 secret 经编码绕过脱敏。密钥来源（`Worker.cs:168` `InitializeSecretMasker`）：secret 变量、服务端下发的正则 MaskHint、运行时 `::add-mask::`。

对应 Mohist：`TaskLogger.write` 内部入口脱敏（见采集管道·Secret 脱敏）。

### 7. 行号是 issue 跳转锚点

GA `PagingLogger._totalLines` 维护单调行号；`Write` 写之前原子预算 `totalLines = _logger.TotalLines + 1` 并返回（`:1102`）。`AddIssue` 把行号存进 `issue.Data["logFileLineNumber"]` + `issue.Data["stepNumber"]`（`ExecutionContext.cs:856`、`Sdk/RSWebApi/Contracts/IssueKeys.cs:11`），前端用双字段深链到该 step 日志的对应行。

对应 Mohist：seq 现阶段做游标分页 + 排序，未来是失败行跳转的现成锚点（见容量控制·seq 的双重用途）。

### 8. stdout 进日志前的命令闸门（Mohist 不需要，但模式可借鉴）

GA 在 stdout 进日志漏斗前有个统一闸门：`if (!CommandManager.TryProcessCommand(...)) { context.Output(line); }`（`OutputManager.cs:77`）。形如 `::set-output name=foo::bar` 的指令行被拦截执行、不作为普通文本输出。GA 进一步用**文件命令**（`$GITHUB_OUTPUT` 等，`FileCommandManager.cs`）替代 stdout 命令，消除注入风险。

Mohist 的 task 是 runner 自己的 ops（git/shell），stdout 里不会有命令指令，**这一层不需要**。但"stdout 进日志前有个统一闸门"的模式——确保只有该进日志的内容才进——在 Mohist 体现为：`runCommand.onLine` 接到 `TaskLogger.write`，由 write 决定是否记（如可选过滤已知噪声行）。

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
