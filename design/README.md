# Design

`design/` 面向开发者和 agent，记录架构边界、领域划分、workflow 机制和跨模块设计约定。面向使用者的文档在 [`../docs/`](../docs/)。

新增或重写的设计正文使用中文；领域标识、字段名、API 和代码符号保留原名。现有英文
设计文档在后续修改时逐步收敛，避免语言迁移与无关设计改动混在一起。

## 全局基础

- [architecture.md](architecture.md) — 运行时边界、控制平面/执行平面职责、放置规则。
- [domain-analysis.md](domain-analysis.md) — 领域分析与上下文映射：子域划分、限界上下文关系、依赖不变量。
- [conventions.md](conventions.md) — 命名、分层、变量等约定。
- [cli.md](cli.md) — 命令面设计契约：句法（资源在前）、命令树形状、资源命名（作用域用 flag、子资源挂父资源下）、动词一致性、唯一入口与全局 flag 约定。
- [testing.md](testing.md) — 测试两条轨道（spec/unit）、外部依赖、时间依赖、fake 入口速查。
- [eventbus.md](eventbus.md) — 事件总线：CloudEvent 订阅契约 + 单分发器可靠 at-least-once 通知。
- [event-protocol.md](event-protocol.md) — 事件协议（**WIP**）：三轴信封模型、业务谱系 stamping 矩阵、匹配表达式（CEL 子集）与 conformance。

## Agent 与执行

- [agent-execution.md](agent-execution.md) — Action、Inline Agent、Mohist Agent、AgentJob、AgentSession 与 Runtime Session 的分层和生命周期所有权。
- [agent-subscriptions.md](agent-subscriptions.md) — Agent 事件路由（**WIP**）：项目级有序路由表，表达式匹配 + first-match/continue 触发 Agent，取代订阅优先级仲裁。

## Runtime 集成

- [runtimes/](runtimes/README.md) — 外部执行后端的进程、SDK、物理 Session、事件与兼容性边界；当前包括 OpenCode。

## Workflow 核心域

- [workflow/actions.md](workflow/actions.md) — action input/output 接口、errorCode、失败恢复编排。
- [workflow/builtin-workflows/](workflow/builtin-workflows/) — 内置 workflow；一个 workflow 一个文件。
- [workflow/profile.md](workflow/profile.md) — profile = template + variables 的加载与合并。
- [workflow/task-dispatch.md](workflow/task-dispatch.md) — Action Input 与 task-level `expect` 的独立模板展开和 dispatch 输入。
- [workflow/recovery.md](workflow/recovery.md) — 失败恢复：recovery 声明、when 匹配、runner 构造恢复任务。
- [workflow/scheduling.md](workflow/scheduling.md) — runner claim、pull、report、supervision。
- [workflow/issue-coordination.md](workflow/issue-coordination.md) — Issue、WorkflowRun、Runner、Session 的跨聚合交互。
- [workflow/boundaries/issue.md](workflow/boundaries/issue.md) — Workflow 与 Issue 的依赖方向和 profile 归属。

## 支撑主题

- [issue-breakdown.md](issue-breakdown.md) — 复合 Issue / 子 Issue 设计（**已定稿，待实装**）：父子模型、状态汇总、复合推进、与 Epic 的隔离约束；多仓库资源见 `docs/repositories.md`。
- [epic-status-revival.md](epic-status-revival.md) — Epic `done` 自动唤醒与 `closed` 拒绝 link 的决策记录（issue-392）。
- [issue-templates.md](issue-templates.md) — 三类 issue 模板（Feature / Bug / Refactor）的 body 结构与设计依据。
- [mobile-pwa.md](mobile-pwa.md) — 移动端 PWA + 推送（**WIP，暂不实现**）：self-host 自治系统的移动端 promise，原 #106 关闭后的方案记录。
- [prompt-management.md](prompt-management.md) — project-scoped prompt 库和 workflow 的关系。
- [runner.md](runner.md) — Runner 聚合信息结构、poll presence 与进程监督契约。
- [task-log.md](task-log.md) — task 执行日志的采集管道、上报通道与存储归属。
- [web-ui.md](web-ui.md) — Web UI 设计边界。
