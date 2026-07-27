# 产品愿景

Mohist 是一座面向 Agent 的软件工厂：把「从想法到交付」变成一条可定义、可执行、
可监督的生产流水线。Issue 进，交付物出；人只在异常时出现。

## 目标

产线定义足够清晰、Agent 执行足够可靠时，一个人的交付产能等于一个小团队。

## 用户怎么使用 Mohist

用户通常在 Slack、IDE 或其他已有场所与外部 Agent 协作，而不是进入 Mohist 工作。
外部 Agent 通过 Mohist Skill 和 `mo` 查询状态、委托工作、执行操作，再把结果带回原来的
交互场所。产品目标是让 90% 以上的日常查询、委托和操作都在这些外部场所完成。

Mohist 的 Issue 与 Workflow 构成执行层，AgentSession 保留其中可追溯的执行会话；这些
对象不是另一个聊天空间。Web UI 是备用操作和可视化平面：用于理解全局与复杂状态，也在
外部 Agent 不可用或需要人工接管时提供完整的关键操作。

## 工厂怎么运转

- **产线即代码** —— workflow definition 用 YAML 定义阶段、任务、检查、审批门、失败恢复。改产线就是改定义，系统本身不变。
- **Issue 是在制品** —— 一切工作以 Issue 为单位进入产线，携带需求、讨论、历史，从 Draft 流到 Done。默认产线：`Draft → Plan → Build → Check → Integrate → Done`。
- **Agent 是工人** —— Inline Agent 直接执行 Workflow task；预定义的 Mohist Agent 可以被启动或响应事件；AgentSession 持久记录会话，中断可恢复。外部 Agent 负责和用户交互，不属于产线里的 Agent 资源。
- **检查与审批门是质检** —— 每个阶段出口由自动检查把关；关键阶段停在审批门，批准才放行。
- **事件路由让产线自转** —— 产线上每个实体都产生事件；路由表订阅事件并触发 Agent 响应——代理审批、处理失败、汇总进展。这是人能够离开回路的机制。
- **升级是人的入口** —— Agent 停手时：通知 + Issue 评论说明卡在哪、试过什么。人只处理异常，不盯流水线。

## 原则

- **Agent-friendly 优先**：外部 Agent 可以通过 Skill 和 `mo` 发现、查询并操作 Mohist；关键能力不能只存在于 Web UI。
- **状态只有一个裁判**：server 裁决产线状态，runner 只上报执行事实。
- **可靠优先于丰富**：能少一个机制就不多一个；每一步在 Issue 上留痕可见。

## 不是什么

- 不是 IDE、聊天工具或协作工作站——用户留在已有交互场所，Mohist 负责让 Issue 流到 Done。
- 不是 CI——CI 验证单次提交，Mohist 推进整个工作单元从需求到集成。

## 方向

每个方向的完整方案见对应产品 spec：

- **人离开回路** —— 监管 Agent 代理审批与失败处理；Issue 关注与 @提及让委托可配置、可收回。见 [Agent 监管](agent-supervision.md)、[Agent 事件路由](event-routing.md)
- **Agent-friendly 交互** —— 外部 Agent 通过稳定的 Skill 与命令面操作 Mohist，人机使用同一套领域动作。见 [Skill 机制](skills.md)、[CLI 参考](cli-reference.md)
- **备用操作与可视化** —— Web UI 汇总全局状态、展示执行证据，并在需要时支持人工操作与接管。见 [Web UI 指南](web-ui.md)
- **更大颗粒的生产计划** —— Epic 自动推进、复合 Issue 跨仓库交付。见 [用 Epic 规划](epics.md)、[复合 Issue 与子 Issue](sub-issues.md)
- **移动监督** —— 手机上看产线状态、收异常推送。见 [移动端 PWA 与推送](mobile-pwa.md)、[Hermes 通知](hermes-notifications.md)

---

本文是未来式：描述产品要成为什么，不是当前实装清单。实装状态见 [README 实装状态表](../README.md#实装状态)。
