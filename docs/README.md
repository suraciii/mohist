# Mohist 文档

这份文档面向**使用者**，按功能板块组织成一条阅读路径。架构与领域分析在 [`../design/`](../design/) 下。

刚接触 Mohist，先看[仓库 README](../README.md) 了解它是什么。

## 板块 1：入门

- [快速上手](getting-started.md) —— 从零启动，看一个 issue 走完全流程
- [核心概念](concepts.md) —— 理解 Mohist 的生产线模型
- [Agent 与 AgentSession](agents.md) —— Inline Agent、Mohist Agent、AgentJob、AgentSession 的层次和关系

## 板块 2：工作流

- [工作流详解](the-workflow.md) —— Draft → Plan → Build → Check → Integrate 每个阶段做什么
- [Workflow Profile](workflow-profiles.md) —— 自定义阶段、任务、检查、审批策略
- [Workflow Definition 参考](workflow-definition.md) —— 编写 definition 的完整语法：stage、task、expect、recovery、模板表达式

## 板块 3：工作管理

- [仓库](repositories.md) —— Project 声明多个仓库作为执行资源，issue 按目标仓库分流
- [Issue 管理](issues.md) —— 创建、启动、审批、恢复、关闭
- [复合 Issue 与子 Issue](sub-issues.md) —— 一个 issue 追踪跨仓库需求，拆成子 issue 各自走 workflow
- [用 Epic 规划](epics.md) —— 把零散 issue 组织成可自动推进的产品目标

## 板块 4：观察与操作

- [Web UI 指南](web-ui.md) —— 看板、详情页、活动流、设置
- [CLI 参考](cli-reference.md) —— `mo` 的目标命令语言、命令地图与交互契约

## 板块 5：执行后端与扩展

- [Action 契约](actions/README.md) —— Workflow Action 的输入、输出与行为；当前包括 `mohist/opencode` 与 `mohist/pi`
- [Runner 指南](runner.md) —— 执行平面怎么跑、怎么调并发
- [Skill 机制](skills.md) —— 用 OpenCode / Claude Code 探索需求，再交给 Mohist
- [Agent 事件路由](event-routing.md) —— 项目级路由表：一条表达式订阅任何实体的事件，按序触发 Mohist Agent 响应
- [Agent 监管](agent-supervision.md) —— 一条命令安装监管 Agent：替你审批、替你修复失败，只有它停手时才轮到你

## 板块 6：部署与运维

- [Self-host 部署](self-host.md) —— NAS / 家用服务器 / 笔记本长跑
- [Hermes 通知](hermes-notifications.md) —— 审批点、失败、完成推送到你的聊天工具
- [故障恢复](troubleshooting.md) —— 失败、blocked、drift 怎么办

## 板块 7：产品方案（WIP）

> 尚未实装、需求已对齐的方案。**这些功能当前不存在**；落地后搬到上面的板块。

- [移动端 PWA 与推送](mobile-pwa.md) —— 手机上看进度、收推送的方案记录（暂缓）
- [可观测性](observability.md) —— 安全地发现运行异常，并保留足够的诊断线索

## 写作约束

- **每节一个目的**：节名即它回答的问题。动机说教、铺垫导言、常识直接删；能列表不段落，能表格不列表。
- **规则单点定义**：一个规则只在一篇里定义，其他篇链接它，不复制。
- **spec 先于实现**：文档记录目标形态——产品该满足什么。实装由 issue 追赶 spec，而非 spec 跟着实装走。文档里出现尚未实装的能力是正常的，由对应 issue 推进落地；落地后无需改动文档（它本就描述目标）。
- **实装差距单列**：当某文档描述的能力与当前代码有显著差距时，在文内单列「实装差距」之类的小节说明现状与对应 issue，而非把正文降级成"当前实装清单"。正文是 spec，差距是脚注。
- **命令自包含**：文档会被 agent 读取并直接执行，所有 shell / CLI 示例必须能独立复制运行，不依赖"把上面那个替换一下"。
- **改前先核对差距**：动手修改任何事实陈述前，先看文内「实装差距」小节是否已标注该处未对齐——避免把 spec 改回现状。
- **WIP 产品方案**：尚未对齐需求、还在探索方向的产品方案收录在「板块 7」，用 frontmatter `status: wip-not-implemented` 标注，用「将支持 / 计划 / （开放）」等表述。需求对齐、spec 定稿后搬到对应板块，移除 WIP 标记。
- **语言统一**：正文使用中文；产品规范术语、配置字段、命令和代码符号保留原名。
- **不用技术语言**：正文不出现 API 端点、字段名、组件类名、源码路径——这些属于 `design/`。唯一例外：文末可以有一行「对应源码：」页脚，指向实现入口。
- **术语一致**：Project / Issue / Workflow / Epic / Inline Agent / Mohist Agent / AgentSession / Skill 等术语在各篇保持一致。

发现过时描述欢迎提 issue。
