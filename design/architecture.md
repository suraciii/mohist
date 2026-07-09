# Mohist Architecture

Mohist 是本地开发工作流自动化系统，不是通用 CI、任务队列、agent 框架或多租户云平台。

## 系统边界

```
User / Web / CLI
       |
       v
Control Plane        owns authoritative state & workflow decisions; exposes API and events
       |
       v
Execution Plane      owns workspace side effects; runs agents and commands; reports facts back
       |
       v
User Project
```

## 核心原则

```
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
```

执行事实和状态裁判分离：Runner 可以产生事实，不能解释事实；Workflow 可以解释事实，不能制造事实。

## 放置规则

| 职责 | 应该放在 | 不应该放在 |
|------|----------|------------|
| 用户入口、命令行交互 | CLI | Server runtime |
| Web 观察和用户操作 | Web UI + API | Runner |
| API、事件、状态查询 | Server | Runner |
| authoritative state | Server | Runner workspace |
| workflow 状态裁判 | Server control plane | Runner |
| runner 注册、心跳、租约校验 | Server control plane | Web UI / CLI |
| workspace 准备和清理 | Runner | Server |
| shell/process/agent 执行 | Runner | Server |
| git merge/rebase 等副作用 | Runner | Server |
| OpenSpec 文件副作用 | Runner | Server |
| 探索/需求澄清对话 | 外部 agent skill | Mohist runtime |
| skill 安装和分发 | CLI | Server workflow runtime |
| 产品流程设计 | product/design docs | architecture doc |
| 领域模型表达 | code | architecture doc |
| 架构边界和原则 | `design/architecture.md` | OpenSpec spec |
| 内置 workflow 的 stages/tasks/checks | `*.workflow.yaml` | stage 设计文档 |

## 判断规则

- 回答"系统状态是什么、下一步该给谁、这个 report 是否可信" → **Control Plane**。
- 回答"如何在项目工作区执行这份 work、如何调用 agent/shell/git" → **Execution Plane**。
- 只关乎用户如何发起或观察操作 → CLI / Web / API interaction，不是 runner。
- 产品流程、阶段语义、用户体验、审批策略 → 产品/设计文档，不是架构文档。
- 定义实体字段、状态枚举、方法签名、数据结构 → 代码，不是架构文档。
- 调整内置 workflow 的阶段/任务/检查/repair 行为 → 改对应 `*.workflow.yaml`；`design/` 只记录引擎机制与跨模块边界，不重复内置 workflow 内容。

**建模边界**：领域模型表达领域逻辑与统一语言，不表达技术实现——先理解用户故事和领域事件，再整理最小模型；数据库表、API shape、技术组件、迁移机制不入模型；同一概念只有一个术语。领域划分与判据见 [`domain-analysis.md`](domain-analysis.md)。

## Control Plane

状态和决策层，回答"系统现在处于什么状态，下一步应该交给谁"：接收用户意图、维护 authoritative state、做调度与状态推进决策、协调 runner（而非替它执行）、发布事件。不把 action 实现细节暴露给 UI，不把 runner workspace 当作 authoritative state。

### 事件通道分两层（SLA 不同，不可混用）

- **Domain reaction（durable）**：聚合状态转移发出的领域事件，经持久化分发器至少一次投递，驱动跨聚合状态推进（如 `WorkflowRunCompleted` → `CompleteIssue`）。这是必须发生的领域反应，不是观察。
- **UI 观察（best-effort）**：SignalR 实时推送等，供 UI 增量更新。系统不依赖 UI 消费这类事件来推进 workflow——UI 断线重连后自己对账。

事件真相由聚合在状态保存的同一事务内追加，分发器是唯一通知者。详见 [`eventbus.md`](eventbus.md)。

## Execution Plane

副作用层，回答"这份 work 如何在项目工作区里被实际执行"：准备和维护 workspace、渲染执行输入、解析并执行 work、启动 agent/shell/process、执行 git 与 OpenSpec side effects、把执行结果归一化为 report。

设计原则：

- Runner 是可替换执行资源：可以失败、下线、重启，也可以扩展为多个。
- Runner 不持有唯一 authoritative state；本地 workspace 状态只有通过 report 上报才可能成为系统事实。
- Runner 不要求 server 信任未校验的 report。
- Runner 不依赖 UI 或人工操作来完成执行闭环。
- Runner 不把执行实现细节泄漏到控制平面之外。

## Report 与 State Ownership

```
Execution side effect
  |
  v
Runner report            ← 事实，不是命令
  |
  v
Ownership validation     ← 无法证明 ownership 的 report 必须被忽略
  |
  v
Workflow decision        ← 在 workflow 上下文中解释事实
  |
  v
Authoritative state      ← 推进状态或继续等待
```

**Report 是事实，不是命令。** Runner 可以说：work completed / work failed / verification passed / verification failed / work produced output。Runner 不可以说：state should advance / issue should be done / approval should be bypassed / retry should be available。

**每个 in-flight work 必须有明确 owner。** 这不是为了分布式优雅性，而是为了应对现实的本地执行问题：runner 断线、进程重启、旧 report 晚到、用户重复启动。不要尝试"聪明地合并" stale report——晚到结果被接受会破坏 workflow 的因果顺序。

## 持久化原则

- Product state、Workflow state 应持久化。
- Runner workspace 默认是可重建执行状态。
- Artifact 是审查证据，不能只存在于内存状态。
- 持有 authoritative state 的 grain 不应 `[Reentrant]`：turn 串行化是 grain 状态安全的来源；事件分发离开发布栈后（见 [`eventbus.md`](eventbus.md)），reentrancy 是无理由的偶然复杂性。

## Agent Skill Boundary

Mohist 不拥有探索式 AI 对话。Explore 是外部 agent 能力，由 Mohist 分发为 skill（如 `mohist-explore`），在 OpenCode、Claude Code、Hermes 等外部 agent 中运行。

- Mohist runtime 不提供 Explore session、Explore chat 或 `/api/explore`。
- 外部 agent skill 可以读取项目、调用 `mo` CLI、创建/更新 issue、写探索记录文件；不直接写 Mohist 数据库，不依赖 Mohist 内部运行时 session。
- Runner 可以调用外部 agent CLI 执行 workflow task——这是 Execution Plane adapter，不是内置 Explore 产品。

## 当前架构约束

- CLI 不合进 Server；Server 是 daemon/API/runtime。
- Action execution 不放进 Server；所有 shell、agent、merge、OpenSpec side effect 都归 Runner。
- 当前假设单机 daemon；actor runtime 主要作为 state model，而不是优先服务分布式部署。可以先接受单进程事件总线，但不能因此把执行逻辑塞回 server——durable 分发器是通知机制，不执行 task/check、不调用 runner/agent。
- OpenSpec spec 不作为架构文档来源；架构边界以 `design/` 下的人工维护文档为准。
