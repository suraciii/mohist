# 产品愿景

Mohist 是一座软件工厂：把「从想法到交付」变成一条可定义、可执行、可监督的生产流水线。issue 进，交付物出；人只在异常时出现。

## 目标

产线定义足够清晰、Agent 执行足够可靠时，一个人的交付产能等于一个小团队。

## 工厂怎么运转

- **产线即代码** —— workflow definition 用 YAML 定义阶段、任务、检查、审批门、失败恢复。改产线就是改定义，系统本身不变。
- **issue 是在制品** —— 一切工作以 issue 为单位进入产线，携带需求、讨论、历史，从 Draft 流到 Done。默认产线：`Draft → Plan → Build → Check → Integrate → Done`。
- **Agent 是工人** —— 任务由 Agent 执行；AgentSession 持久化，中断可恢复；预定义的 Mohist Agent 可被产线任务直接引用（`use: mohist/agent`）。
- **检查与审批门是质检** —— 每个阶段出口由自动检查把关；关键阶段停在审批门，批准才放行。
- **事件路由让产线自转** —— 产线上每个实体都产生事件；路由表订阅事件并触发 Agent 响应——代理审批、处理失败、汇总进展。这是人能够离开回路的机制。
- **升级是人的入口** —— Agent 停手时：通知 + issue 评论说明卡在哪、试过什么。人只处理异常，不盯流水线。

## 原则

- **Agent 一级公民**：有名字、能被分派、能被 @提及、也能被移除——不是外挂脚本。
- **状态只有一个裁判**：server 裁决产线状态，runner 只上报执行事实。
- **可靠优先于丰富**：能少一个机制就不多一个；每一步在 issue 上留痕可见。

## 不是什么

- 不是 IDE 或聊天工具——Mohist 不管 prompt 怎么写，管 issue 怎么流到 Done。
- 不是 CI——CI 验证单次提交，Mohist 推进整个工作单元从需求到集成。

## 方向

每个方向的完整方案见对应产品 spec：

- **人离开回路** —— 监管 Agent 代理审批与失败处理；issue 关注与 @提及让委托可配置、可收回。见 [Agent 监管](agent-supervision.md)、[Agent 事件路由](event-routing.md)
- **更大颗粒的生产计划** —— Epic 自动推进、复合 issue 跨仓库交付。见 [用 Epic 规划](epics.md)、[复合 Issue 与子 Issue](sub-issues.md)
- **移动监督** —— 手机上看产线状态、收异常推送。见 [移动端 PWA 与推送](mobile-pwa.md)、[Hermes 通知](hermes-notifications.md)

---

本文是未来式：描述产品要成为什么，不是当前实装清单。实装状态见 [README 实装状态表](../README.md#实装状态)。
