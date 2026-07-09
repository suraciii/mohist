# Task 执行日志

task 执行过程日志（TaskLog）的设计。参照 GitHub Actions Runner 的 step log——可折叠、逐行、可实时流式的执行过程记录。

## 问题

task 执行中运维动作的 stdout/stderr 被丢弃（`prepareWorkspace`、`checkBranchStability`、`git()`、`enforceCleanWorktree` 等），只有 `action()` 内的 `runCommand` 以 `combinedOutput` 聚合摘要。用户只能看到 task 终态结果（status + message + output JSON），看不到执行过程。

已有但不覆盖此问题的：

| 概念 | 覆盖 | 不覆盖 |
|------|------|--------|
| AgentSession transcript | agent 对话流 | ops 命令过程（git/shell） |
| Artifact | 产出文件 | 执行过程 |
| task output JSON | 结构化结果 | 逐行过程 |

## 归属边界

**TaskLog 属于 Runner 子域的执行痕迹，不进 WorkflowRun。**

```
              不经过                             经过
                                                 │
POST /api/{ownerKind}/{ownerId}/work/{workId}/task-log    POST /api/runner/{runnerId}/report
  → 写入独立 TaskLogStore (Runner 子域)                    → WorkResult → WorkflowGrain 裁定
  → WorkflowRun 永不感知                                    → WorkResult 永不携带日志
```

端点用 owner 定位（`ownerKind` = `workflow-runs` | `agent-jobs`），对齐 artifact 上传路径。

判据：

| 检验 | TaskLog | 结论 |
|------|---------|------|
| 是 WorkflowRun 状态裁定的输入？ | 否 | 不进 WorkResult |
| WorkflowRun 行为签名需要它？ | 否 | 不进 WorkflowRun 聚合 |
| 是审查证据（类比 Artifact）？ | 是 | 独立持久化，通过 workId 关联 |

## 概念模型

```
TaskRun (1)                                  ← 已有，在 WorkflowRun 内
 ├─ status / message / output               ← 终态结果（裁定输入）
 ├─ Artifacts                               ← 产出物
 ├─ AgentSession transcript                 ← agent 对话（仅 agent task）
 └─ TaskLog (1)                             ← 新增：执行过程日志
      └─ LogEntry[]
           ├  seq            # 单调递增行号，游标分页 + 失败定位锚点
           ├  timestamp
           ├  source         # workspace-prep | branch-check | action:rebase | cleanup | ...
           └  text
```

**不区分 stdout/stderr**（对齐 GA）：ops 输出合流，靠 `source` + 文本内容定位，不靠 stream 维度。

TaskLog 与 TaskRun 是 1:1，但存储和生命周期完全独立——TaskLog 是 Runner 产生的痕迹事实，TaskRun 是 Workflow 裁定的状态。

## 采集管道（Runner 侧）

### 单一汇聚点

所有输出经过 `ActionContext.log.write(source, text)`，不各处拼 `combinedOutput`。对标 GA 的 `ExecutionContext.Write`——所有面向用户的文本都过这一个方法，secret 脱敏、行号自增、缓冲只写一次：

```ts
interface TaskLogger {
  write(source: string, text: string): number  // 返回 seq
}

interface ActionContext {
  log: TaskLogger
}
```

### Secret 脱敏

`TaskLogger.write` 入口第一步即脱敏，对标 GA `SecretMasker.MaskSecrets`——落盘/入队/推流时数据已是脱敏后：

```ts
write(source: string, text: string): number {
  const masked = this.maskSecrets(text)        // ① 入口脱敏
  const seq = ++this._seq                       // ② 自增行号
  this._collector.append({ seq, timestamp: this._clock.now(), source, text: masked })
  return seq
}
```

### runCommand 逐行输出

当前 `runCommand` 只返回聚合 stdout/stderr。升级为可选 `onLine` 回调逐行输出，保证三重防丢行（对标 GA `ProcessInvoker`）：`EndOfStream` + `ReadLine` 捕获无尾换行末行、进程退出后无条件再 drain、读流超时强杀。

```ts
interface RunCommandOptions {
  onLine?: (line: string) => void
}
```

### TaskLogCollector

每个 work item 一个 collector，缓冲日志行。对标 GA `JobServerQueue` 的批量 drain——采集端只 append，flush 端定时/按量批量上报：

```text
TaskLogCollector (per work)
  ├─ buffer: LogEntry[]
  ├─ flush()                     # 批量 POST
  │   ├─ Phase 1：task 结束一次性 flush
  │   └─ Phase 2：执行中定时/按量 flush（实时流式）
  └─ capacity limit              # 超限截断：丢头留尾，错误上下文优先
```

## 上报通道

独立端点，不走 report：

```text
Runner 执行 work:
  采集 ops 输出 → TaskLogCollector
  ──(执行中/完成后)──▶ POST /api/{ownerKind}/{ownerId}/work/{workId}/task-log
                        独立端点，写独立 store
  ──(终态)──────────▶ POST /api/runner/{runnerId}/report
                        照旧进 WorkflowGrain 裁定
```

两期模式：

| 阶段 | 说明 |
|------|------|
| Phase 1 终态批量 | task 完成前一次性 POST，task 结束即可看完整日志 |
| Phase 2 实时流式 | 执行中分批 flush + SignalR 推送，边执行边看（best-effort，落库是权威） |

## 存储与查询（Server 侧）

### 存储结构

Phase 1 直接对标 `WorkflowArtifactUploadService`——端点 → store → DbSet.Add → SaveChanges，不经任何 grain：

```text
TaskLogEntries
  ├ Id (PK, 自增 long)
  ├ OwnerKind (索引)        # "workflow" | "agent-job"
  ├ OwnerId (索引)          # workflowRunId 或 agentJobId
  ├ WorkId (索引)
  ├ Seq                     # 该 task 内单调递增
  ├ Timestamp
  ├ Source                  # workspace-prep / action:rebase / cleanup / ...
  └ Text                    # nvarchar max
```

无 Stream 列（stdout/stderr 合流）。索引 `(OwnerKind, OwnerId, WorkId, Seq)` 支撑游标分页。

### 查询 API

```text
GET /api/projects/{projectId}/issues/{number}/workflow/tasks/{taskId}/logs?cursor=&limit=
  → { lines: [{seq,timestamp,source,text}], nextCursor, truncated }
```

走 issue 路径（对齐 artifact 查询），语义是"按 task 查执行痕迹"。

### 容量控制

- 单 task log 上限（如 256KB / 5000 行），超限截断标记 `truncated`。
- **丢头留尾**：超限时丢弃头部行，保留尾部错误上下文。失败 task 的尾部行是定位关键。
- seq 保持单调连续（被丢弃的 seq 不重用），游标分页稳定。

## 与现有概念的关系

| 概念 | 回答的问题 | 归属 | TaskLog 关系 |
|------|-----------|------|-------------|
| TaskLog（新） | task 执行**过程**发生什么 | Runner 子域 | — |
| AgentSession transcript | agent **对话**了什么 | Session 子域 | 互补：agent task = transcript + log |
| Artifact | task **产出**什么文件 | 审查证据 | 同级证据 |
| task output JSON | task 的**结构化结果** | Workflow 裁定输入 | 互补：log 给过程，output 给结论 |

## 分期落地

| 阶段 | 范围 | 收益 | 改动面 |
|------|------|------|--------|
| Phase 1 终态日志 | runCommand/git() 加 onLine → ActionContext.log → 批量 POST → 存表 → Web 展示 | 失败可定位过程 | executor + runCommand + 1 表 + 1 端点 + Web 面板 |
| Phase 2 实时流式 | 执行中分批 flush → SignalR 推 taskLog.delta → Web 追加 | 执行中可见 | 新增流式通道 |
| Phase 3 体验增强 | 搜索、下载、级别过滤、失败行跳转 | 对齐 GA 完整体验 | 纯前端 |

## 范围外

- 不改 report / WorkResult 结构（日志走独立通道）。
- 不改 WorkflowRun 聚合根（永不感知 task log）。
- 不替代 AgentSession transcript（agent 对话痕迹已有独立链路）。
- 不做分布式日志聚合 / 多 runner 日志归档（当前单机假设）。
- 不改全局控制平面/执行平面边界（见 architecture.md）。

## 差距脚注

正文是 spec，以下是现状差距，收敛后删：

- runCommand 无 `onLine`，输出以 `combinedOutput` 聚合返回。
- 无 TaskLogCollector / TaskLogStore / task-log 端点，表未建。
- 无 TaskLogger 实现，secret 脱敏未在日志汇聚点统一做。
