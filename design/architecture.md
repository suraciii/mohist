# Mohist Architecture

本文是 Mohist 的架构边界文档。目标是让人或 AI agent 在修改系统时能快速判断：某个职责应该放在哪里，不应该放在哪里。

本文只记录架构相关信息：运行时边界、职责分层、放置规则、设计原则和一致性约束。

不记录：

- 领域模型定义。模型应该由代码表达。
- 产品流程设计。产品设计应放在产品文档中。
- 类、方法、路由、字段的说明。
- 代码结构可以直接看出的调用关系。
- 当前实现清单或临时迁移进度。
- action 列表、API 列表、文件路径清单。

## 系统边界

Mohist 是本地开发工作流自动化系统，不是通用 CI、任务队列、agent 框架或多租户云平台。

架构关注运行时边界：用户交互、控制平面、执行平面、项目工作区分别承担不同职责。

```
User / Web / CLI
       |
       v
Control Plane
  owns authoritative state
  owns workflow decisions
  exposes API and events
       |
       v
Execution Plane
  owns workspace side effects
  runs agents and commands
  reports facts back
       |
       v
User Project
```

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
| 默认 workflow 的 stages/tasks/checks | `mohist-default.workflow.yaml` | stage 设计文档 |

## 判断规则

如果一个改动需要回答“系统状态是什么、下一步该给谁、这个 report 是否可信”，它属于 Control Plane。

如果一个改动需要回答“如何在项目工作区执行这份 work、如何调用 agent/shell/git”，它属于 Execution Plane。

如果一个改动只是用户如何发起或观察操作，它属于 CLI/Web/API interaction，而不是 runner。

如果一个改动是产品流程、阶段语义、用户体验或审批策略，优先放到产品/设计文档，而不是架构文档。

如果一个改动是在定义实体字段、状态枚举、方法签名或数据结构，优先用代码表达，而不是架构文档。

如果一个改动是在调整默认 workflow 的阶段、任务、检查或 repair 行为，优先修改 `packages/server/src/Mohist.Server/Issue/WorkflowProfiles/mohist-default.workflow.yaml`。`design/` 只记录 workflow engine 机制、身份约定和跨模块边界，不重复默认 workflow 内容。

## Modeling Boundary

领域模型应该表达领域逻辑和统一领域语言，而不是技术实现。

应该进入领域模型：

- 用户故事中稳定出现的业务概念。
- 领域规则和不变量。
- 领域事件和状态变化语义。
- 用户、产品、系统都能用同一种语言理解的概念。

不应该进入领域模型：

- 数据库表结构。
- API route、SSE event name、HTTP payload shape。
- handler registry、task loader、runner adapter 等技术组件。
- 迁移机制、legacy bridge、测试文件或具体类名。
- 从当前代码结构反推出来但用户语言里不存在的概念。

领域建模顺序：先理解用户故事和领域事件，再整理最小模型；不要先建表、先设计 API，或从技术结构反推模型。

同一概念在模型中只能有一个术语。不要把实现词、反向视角或已有冲突语义混进领域语言。

## 核心原则

```
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
```

含义：执行事实和状态裁判分离。Runner 可以产生事实，不能解释事实；Workflow 可以解释事实，不能制造事实。

## Agent Skill Boundary

Mohist 不拥有探索式 AI 对话。Explore 是外部 agent 能力，由 Mohist 分发为 skill（例如 `mohist-explore`），在 OpenCode、Claude Code、Hermes 等外部 agent 中运行。

边界规则：

- Mohist runtime 不提供 Explore session、Explore chat 或 `/api/explore`。
- 外部 agent skill 可以读取项目、调用 `mo` CLI、创建/更新 issue、写入普通探索记录文件。
- 外部 agent skill 不直接写 Mohist 数据库，不依赖 Mohist 内部运行时 session。
- Runner 可以调用外部 agent CLI 执行 workflow task；这是 Execution Plane adapter，不是内置 Explore 产品。

## Control Plane

Control Plane 是 Mohist 的状态和决策层。它回答“系统现在处于什么状态，下一步应该交给谁”。

职责：

- 接收用户意图。
- 维护 authoritative state。
- 做出调度和状态推进决策。
- 协调 runner，而不是执行 runner 的工作。
- 发布用于 UI 观察的事件。

不负责：

- 不运行 shell command。
- 不启动 agent process。
- 不做 git merge/rebase 等有副作用操作。
- 不把 action 实现细节暴露给 UI。
- 不把 runner workspace 当作 authoritative state。

决策原则：

```
reported fact
  |
  v
validate ownership
  |
  v
interpret in workflow context
  |
  v
advance state or wait
```

事件原则：事件用于观察，不用于执行控制。UI 可以用事件更新视图，但系统不能依赖 UI 消费事件来推进 workflow。

## Execution Plane

Execution Plane 是 Mohist 的副作用层。它回答“这份 work 如何在项目工作区里被实际执行”。

职责：

- 准备和维护执行 workspace。
- 渲染执行输入。
- 解析并执行 work。
- 启动 agent、shell、process。
- 执行 git 和 OpenSpec side effects。
- 把执行结果归一化为 report。

不负责：

- 不决定 workflow 是否进入下一阶段。
- 不决定哪些用户动作可用。
- 不直接修改 issue authoritative state。
- 不把本地 workspace 状态当成系统事实，除非通过 report 上报。

设计原则：

- Runner 是可替换执行资源。
- Runner 可以失败、下线、重启，也可以将来扩展为多个 runner。
- Runner 不持有唯一 authoritative state。
- Runner 不要求 server 信任未校验的 report。
- Runner 不依赖 UI 或人工操作来完成执行闭环。
- Runner 不把执行实现细节泄漏到控制平面之外。

## State Ownership

Control Plane 拥有 authoritative state。

Execution Plane 拥有 workspace side effects，但这些 side effects 只有通过 report 被 Control Plane 接受后，才成为系统状态的一部分。

```
Execution side effect
  |
  v
Runner report
  |
  v
Ownership validation
  |
  v
Workflow decision
  |
  v
Authoritative state
```

## Report 语义

Report 是事实，不是命令。

Runner 可以说：

- work completed
- work failed
- verification passed
- verification failed
- work produced output

Runner 不可以说：

- state should advance
- issue should be done
- approval should be bypassed
- retry should be available

## Work Ownership

每个 in-flight work 必须有明确 owner。

这不是为了分布式优雅性，而是为了避免现实中的本地执行问题：runner 断线、进程重启、旧 report 晚到、用户重复启动。

无法证明 ownership 的 report 必须被忽略。不要尝试“聪明地合并” stale report。晚到结果如果被接受，会破坏 workflow 的因果顺序。

## 持久化原则

- Product state 应持久化。
- Workflow state 应持久化。
- Runner workspace 默认是可重建执行状态。
- Artifact 是审查证据，不能只存在于内存状态。

## 当前架构约束

- CLI 不合进 Server；Server 是 daemon/API/runtime。
- Action execution 不放进 Server；所有 shell、agent、merge、OpenSpec side effect 都归 Runner。
- Explore 不放进 Server；探索通过外部 agent skill 完成。
- 当前假设单机 daemon；actor runtime 主要作为 state model，而不是优先服务分布式部署。
- 可以先接受单进程事件总线，但不能因此把执行逻辑塞回 server。
- OpenSpec spec 不作为架构文档来源；架构边界以 `design/` 下的人工维护文档为准。
