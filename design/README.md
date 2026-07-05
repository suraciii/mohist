---
purpose: "Design 文档索引：按全局原则、Workflow 核心域、支撑主题组织。"
style: ["短索引，只说明入口。"]
---

# Design

`design/` 面向开发者和 agent，记录架构边界、领域划分、workflow 机制和跨模块设计约定。面向使用者的文档在 [`../docs/`](../docs/)。

## 全局基础

- [architecture.md](architecture.md) — 运行时边界、控制平面/执行平面职责、放置规则。
- [domain-analysis.md](domain-analysis.md) — 问题空间和子域划分。
- [context-map.md](context-map.md) — 限界上下文和模型依赖方向。
- [conventions.md](conventions.md) — 命名、分层、变量等约定。
- [cli.md](cli.md) — 命令面设计契约：句法（资源在前）、命令树形状、资源命名（作用域用 flag、子资源挂父资源下）、动词一致性、唯一入口与全局 flag 约定。
- [testing.md](testing.md) — 测试两条轨道（spec/unit）、外部依赖、时间依赖、fake 入口速查。
- [eventbus.md](eventbus.md) — 事件总线边界和 CloudEvent 约定（as-is，当前运行时）。
- [eventbus-v2.md](eventbus-v2.md) — 事件总线目标态：复用已落盘事件表 + 单分发器可靠 at-least-once 通知（设计已收敛，**未交付**，跟踪 epic #36）。
- [agent-subscriptions.md](agent-subscriptions.md) — Agent 事件订阅（**WIP**）：Agent 监听 CloudEvent、按订阅响应提示词自动启动。归属 Agent 上下文，消费 PL；handler 只读信封、Agent 用 `mo workflow show <runId>` 自拉上下文；前置依赖 mo workflow 命令套件已由 issue #381 交付。

## Workflow 核心域

- [workflow/actions.md](workflow/actions.md) — action input/output 接口、errorCode、失败恢复编排。
- [workflow/builtin-workflows/](workflow/builtin-workflows/) — 内置 workflow；一个 workflow 一个文件。
- [workflow/profile.md](workflow/profile.md) — profile = template + variables 的加载与合并。
- [workflow/task-dispatch.md](workflow/task-dispatch.md) — task.with 模板展开和 dispatch 输入。
- [workflow/scheduling.md](workflow/scheduling.md) — runner claim、pull、report、supervision。
- [workflow/issue-coordination.md](workflow/issue-coordination.md) — Issue、WorkflowRun、Runner、Session 的跨聚合交互。
- [workflow/boundaries/issue.md](workflow/boundaries/issue.md) — Workflow 与 Issue 的依赖方向和 profile 归属。

## 支撑主题

- [issue-breakdown.md](issue-breakdown.md) — Issue 拆分 / sub-issue 方案（**WIP，暂不实现**）：与 Epic 重叠、#281 已列为 Non-Goal 的决策记录与开放问题。
- [issue-templates.md](issue-templates.md) — 三类 issue 模板（Feature / Bug / Refactor）的 body 结构与设计依据。
- [mobile-pwa.md](mobile-pwa.md) — 移动端 PWA + 推送（**WIP，暂不实现**）：self-host 自治系统的移动端 promise，原 #106 关闭后的方案记录。
- [prompt-management.md](prompt-management.md) — project-scoped prompt 库和 workflow 的关系。
- [runner.md](runner.md) — Runner 聚合信息结构与自报 status。
- [task-log.md](task-log.md) — task 执行日志的采集管道、上报通道与存储归属。
- [web-ui.md](web-ui.md) — Web UI 设计边界。
