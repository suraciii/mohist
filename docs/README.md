# Mohist 文档

这份文档面向**使用者**，按功能板块组织成一条阅读路径。架构与领域分析在 [`../design/`](../design/) 下。

刚接触 Mohist，先看[仓库 README](../README.md) 了解它是什么。

## 板块 1：入门

> 先跑通一个 issue，再理解 Project / Issue / Workflow / Epic 这些名词。读完能回答"这是啥、怎么转起来"。

- [快速上手](getting-started.md) —— 从零启动，看一个 issue 走完全流程
- [核心概念](concepts.md) —— 五个名词，理解了就理解了 Mohist

## 板块 2：工作流

> Mohist 的核心在这。你在快速上手里见过一个 issue 跑完，这里深入它怎么自治地流过每个阶段，以及怎么定制它。

- [工作流详解](the-workflow.md) —— Draft → Plan → Build → Check → Integrate 每个阶段做什么
- [Workflow Profile](workflow-profiles.md) —— 自定义阶段、任务、检查、审批策略

## 板块 3：工作管理

> 日常工作：创建和推进 issue，把多个 issue 组织成 Epic 做产品规划。

- [Issue 管理](issues.md) —— 创建、启动、审批、恢复、关闭
- [用 Epic 规划](epics.md) —— 把零散 issue 组织成可自动推进的产品里程碑

## 板块 4：观察与操作

> 怎么通过 Web UI 看进度，怎么用 `mo` 命令行操作。

- [Web UI 指南](web-ui.md) —— 看板、详情页、活动流、设置
- [CLI 参考](cli-reference.md) —— `mo` 命令完整说明

## 板块 5：执行与扩展

> 执行后端怎么配，怎么用外部 agent 探索需求、产出 issue。

- [Runner 指南](runner.md) —— 执行平面怎么跑、怎么调并发
- [Skill 机制](skills.md) —— 用 OpenCode / Claude Code 探索需求，再交给 Mohist

## 板块 6：部署与运维

> 在你的机器上长跑，以及出问题时怎么办。

- [Self-host 部署](self-host.md) —— NAS / 家用服务器 / 笔记本长跑
- [故障恢复](troubleshooting.md) —— 失败、blocked、drift 怎么办

## 板块 7：产品方案（WIP）

> 还没实装、但已对齐需求的产品方案。**这些功能当前不存在**，文档记录的是方向与用户需求，不是可用能力。落地后会搬到上面的板块并去掉 WIP 标记。

- [Agent 事件订阅](agent-subscriptions.md) —— Agent 监听 issue/workflow 事件、按订阅响应提示词自动启动

## 写作约束

- **代码为准**：文档只写当前版本真实可用的能力；任何事实陈述都要能在源码里找到对应。
- **代码没有的不写**：即使 UI、数据库字段、handler 接口有暗示，但 service 层无真实读写逻辑的，一律视为未实装，不写进文档。
- **改前先验证**：动手修改任何事实陈述前，先重读文末"对应源码"指向的代码，确认事实未变——产品变更快，旧描述可能已漂移。
- **命令自包含**：文档会被 agent 读取并直接执行，所有 shell / CLI 示例必须能独立复制运行，不依赖"把上面那个替换一下"。
- **不写愿景**：还没实现的功能不写进文档。
- **WIP 产品方案例外**：尚未实装但已对齐需求的产品方案可以收录，但必须满足三条——① 收录在「板块 7：产品方案（WIP）」；② 正文顶部用 frontmatter 标注 `status: wip-not-implemented`，并在正文开头用醒目提示写明「尚未实现」；③ 不写已实装语气的命令/操作，只用「将支持 / 计划 / （开放）」等表述。落地实装后搬去对应板块，移除 frontmatter 与 WIP 提示。
- **术语一致**：Project / Issue / Workflow / Epic / Skill 等术语在各篇保持一致。

发现过时描述欢迎提 issue。
