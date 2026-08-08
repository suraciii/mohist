# 产品愿景

Mohist 是一座面向 Agent 的软件工厂：把「从想法到交付」变成一条可定义、可执行、
可监督的生产流水线。Issue 进，交付物出；人只在异常时出现。

## 目标

产线定义足够清晰、Agent 执行足够可靠时，一个人的交付产能等于一个小团队。

## 用户怎么使用 Mohist

用户通常留在 Slack、IDE 或其他已有场所，而不是进入 Mohist 工作。产品目标是让 90%
以上的日常查询、委托和操作都在这些外部场所完成，但“交互发生在外部”不等于“Agent
必须运行在 Mohist 之外”。

Mohist 支持两种互补路径：

- 已配置的 **Mohist Agent** 可以直接在 Web UI 或 CLI 中使用，也可以通过 Agent 接入
  出现在 Slack 等外部场所。无论入口在哪里，用户调用的都是同一个 Agent；它的 Instructions、
  执行配置和 Skills 只在 Mohist 配置一次，首次启动由 AgentJob 承担，会话由 AgentSession
  记录。
- 已经运行在 IDE、Slack 或其他工具中的**外部 Agent**可以通过 Mohist Skill 和 `mo`
  查询状态、委托工作、执行操作，再把结果带回它原有的会话。它不因此变成 Mohist Agent。

Mohist 的 Issue 与 Workflow 构成执行层，AgentSession 保留其中可追溯的执行会话；这些
对象不是要求用户迁入的新工作站。Web UI 是备用操作和可视化平面：用于配置和直接测试
Agent、理解全局与复杂状态，也在外部入口不可用或需要人工接管时提供完整的关键操作。

## 工厂怎么运转

- **产线即代码** —— workflow definition 用 YAML 定义阶段、任务、检查、审批门、失败恢复。改产线就是改定义，系统本身不变。
- **Issue 是在制品** —— 一切工作以 Issue 为单位进入产线，携带需求、讨论、历史，从 Draft 流到 Done。默认产线：`Draft → Plan → Build → Check → Integrate → Done`。
- **Agent 是工人** —— Inline Agent 直接执行 Workflow task；预定义的 Mohist Agent 可以从 Web、CLI、Agent 接入、事件或评论提及启动；AgentSession 持久记录会话，中断可恢复。外部 Agent 也可以通过 Skill 委托产线，但不属于 Mohist 的 Agent 资源。
- **检查与审批门是质检** —— 每个阶段出口由自动检查把关；关键阶段停在审批门，批准才放行。
- **事件路由让产线自转** —— 产线上每个实体都产生事件；路由表订阅事件并触发 Agent 响应——代理审批、处理失败、汇总进展。这是人能够离开回路的机制。
- **升级是人的入口** —— Agent 停手时：通知 + Issue 评论说明卡在哪、试过什么。人只处理异常，不盯流水线。

## 原则

- **Agent 本身可用**：Mohist Agent 必须先能独立配置、启动、继续对话和读取结果；Slack 等接入不能成为它能够工作的前提。
- **一个 Agent，多种入口**：Web、CLI、Slack 和自动化调用同一个 Agent 能力。入口只负责身份、协议和呈现，不保存另一份 Instructions、模型或 Skills。
- **Agent-friendly 优先**：Mohist Agent 有稳定的调用接口；外部 Agent 可以通过 Skill 和 `mo` 发现、查询并操作 Mohist；关键能力不能只存在于 Web UI。
- **状态只有一个裁判**：server 裁决产线状态，runner 只上报执行事实。
- **可靠优先于丰富**：能少一个机制就不多一个；每一步在 Issue 上留痕可见。

## 不是什么

- 不是 IDE、聊天工具或协作工作站——Mohist 可以承载 AgentSession，但不要求用户把日常协作迁入 Mohist；用户留在已有交互场所，Mohist 负责执行和留痕。
- 不是 CI——CI 验证单次提交，Mohist 推进整个工作单元从需求到集成。

## 方向

每个方向的完整方案见对应产品 spec：

- **人离开回路** —— 监管 Agent 代理审批与失败处理；Issue 关注与 @提及让委托可配置、可收回。见 [Agent 监管](agent-supervision.md)、[Agent 事件路由](event-routing.md)
- **独立可用的 Mohist Agent** —— Agent 在 Web、CLI 和外部接入中保持同一身份、配置、工作与会话模型。见 [Agent 与 AgentSession](agent-sessions.md)
- **富 Agent 与会话树** —— Agent 在自己的会话里 spawn 子会话，把运行时才能看清形状的任务分解出去；Mohist 承载树、消息与生命周期，工作流由 Agent 规划。见 [Subagent 与会话树](subagents.md)
- **把 Agent 接入已有场所** —— 已配置的 Mohist Agent 可以作为独立身份加入 Slack；Slack 只承担交互适配。见 [Slack](slack.md)
- **外部 Agent 友好** —— 第三方 Agent 通过稳定的 Skill 与命令面操作 Mohist，人机使用同一套领域动作。见 [Skill 机制](skills.md)、[CLI 参考](cli-reference.md)
- **备用操作与可视化** —— Web UI 汇总全局状态、展示执行证据，并在需要时支持人工操作与接管。见 [Web UI 指南](web-ui.md)
- **更大颗粒的生产计划** —— Epic 自动推进、复合 Issue 跨仓库交付。见 [用 Epic 规划](epics.md)、[复合 Issue 与子 Issue](sub-issues.md)
- **移动监督** —— 手机上看产线状态、收异常推送。见 [移动端 PWA 与推送](mobile-pwa.md)、[Hermes 通知](hermes-notifications.md)

---

本文是未来式：描述产品要成为什么，不是当前实装清单。实装状态见 [README 实装状态表](../README.md#实装状态)。
