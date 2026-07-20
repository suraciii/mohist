# Design

`design/` 面向开发者和 agent，记录架构边界、领域划分、workflow 机制和跨模块设计约定。面向使用者的文档在 [`../docs/`](../docs/)。

新增或重写的设计正文使用中文；领域标识、字段名、API 和代码符号保留原名。现有英文
设计文档在后续修改时逐步收敛，避免语言迁移与无关设计改动混在一起。

## 写设计 Spec

把设计 spec 作为系统如何实现目标行为的权威说明。让人、Agent 和实现读出同一套模型
和行为。
不要让 Agent 猜规则。不要让当前代码替目标设计做决定。

### 先定模型

- 先写这个概念是什么。再写它不是什么。
- 写清谁拥有它、在哪里生效、怎样识别、何时创建或结束，以及什么必须一直成立。
- 只引入有业务意义的概念。没有独立身份、行为或规则，就不要增加新名词。
- 只保留当前行为需要的字段。不要为可能有用的未来能力提前增加资源、作用域或 API。
- 不要因为几份数据形状相同，就发明共同的领域概念。
- 不要把读取顺序、存储结构或代码调用链当成领域模型。
- 只在解释代码边界时写 provider、resolver、manager。不要把它们当成领域名词。
- 让一个名词只表达一个意思。发现重名或多义时，立即改名或拆开。
- 把所有者、作用域或生命周期不同的资源分开。不要用笼统的 `config` 把它们绑在一起。
- 只在一个文档里定义一个规则。其他文档链接它，不要复制它。

### 写清语义

- 写确定规则。不要只写设计意图。
- 只记录会影响后续修改的设计原因。不要记录完整讨论过程。
- 写出完整顺序。明确谁先、谁后、谁覆盖谁。
- 写出解析时机。明确什么会实时生效，什么会在启动时固定。
- 写出写入目标。明确一次操作修改哪个资源，不修改哪些资源。
- 写出失败行为。拒绝非法状态，不要静默忽略错误。
- 用伪代码表达确定算法。让读者可以按步骤实现。
- 用输入和输出表达合并、回退、选择和状态变化。
- 对相同语义使用相同接口。不要因为调用方不同就复制 API。
- 把调用方限制写成参数限制。不要把它包装成新的领域能力。
- 先写行为，再写 YAML、JSON、API DTO 或数据库怎样表达它。
- 让 schema 和 validator 判断 DSL 是否有效。不要让 LLM 猜。

### 选对表达

- 优先写短句。一个句子只表达一个规则。
- 优先使用领域名词和产品名词。只在实现设计中使用技术名词。
- 使用规范名称。保持大小写、单复数和字段路径一致。
- 关系或流程用文字说不清时，使用 PlantUML。文字已经清楚时，不要画图。
- 只在图中画真实概念。给每条箭头写清含义。
- 在正文中写出关键规则。不要让图片成为唯一真源。
- 需要表达确定计算时，使用伪代码。
- 需要消除歧义时，使用最小输入/输出示例。
- 让示例像测试。只保留能区分不同理解的例子。
- 保证 YAML、JSON、命令和 API 示例可以被解析或直接执行。

### 使用最小结构

从下面的结构开始。没有内容时，删除对应小节。不要为了对称增加空小节。

```text
# Name

一句话定义。
一句话边界。

## Model
写资源、所有权、引用和最小数据形状。

## Semantics
写选择、合并、状态变化、时机、错误和接口。

## Examples
写少量输入和预期输出。

## Status
写开放问题和当前实现差距。
```

把 API、Writes、Merge 等放在 `Semantics` 的子小节。只有内容足够复杂时再拆成独立
小节。

### 提交前检查

- 确认读者能回答：它是什么？谁拥有？作用域是什么？
- 确认读者能回答：怎样选择？怎样读取？怎样修改？
- 确认读者能回答：冲突时谁覆盖谁？什么时候生效？
- 确认读者能回答：失败时发生什么？哪些状态不允许出现？
- 确认正文描述目标设计。把当前实现差距移到 `Status`。
- 删除重复规则、无行为的抽象和只解释代码步骤的文字。
- 检查图、伪代码、示例和正文是否表达同一套语义。
- 让另一个 Agent 只读 spec。它还必须读代码才能实现时，补齐 spec。
- 让两个独立 Agent 从 spec 推导行为。它们得到不同结果时，消除歧义。

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
- [event-routing.md](event-routing.md) — Agent 事件路由（**WIP**）：项目级有序路由表，表达式匹配 + first-match/continue 触发 Agent，取代订阅优先级仲裁。

## Runtime 集成

- [runtimes/](runtimes/README.md) — 外部执行后端的进程、SDK、物理 Session、事件与兼容性边界；当前包括 OpenCode。

## Workflow 核心域

- [workflow/definition.md](workflow/definition.md) — Workflow Definition DSL（**WIP**）：语义模型（Expect 一等建模）、唯一权威校验器（规则目录、三处入口含 `mo` 本地校验）与实现侧语义索引；语法权威在 [`docs/workflow-definition.md`](../docs/workflow-definition.md)。
- [workflow/actions.md](workflow/actions.md) — Action 插件模型(**WIP**):manifest 契约、输入单通道、结构化 output、能力注入、catalog 校验、失败恢复编排。
- [workflow/builtin-workflows.md](workflow/builtin-workflows.md) — 内置 workflow（local / github-pr）的设计要点；yaml 定义是真源。
- [workflow/profile.md](workflow/profile.md) — Workflow Profile（**WIP**）：Project-scoped collection、默认选择、Issue override 与 Run snapshot。
- [workflow/variables.md](workflow/variables.md) — Workflow Variables（**WIP**）：Project / Issue / Run 资源、合并、动态生效与 `setVars` 语义。
- [workflow/task-dispatch.md](workflow/task-dispatch.md) — Action Input 与 task-level `expect` 的独立模板展开和 dispatch 输入。
- [workflow/recovery.md](workflow/recovery.md) — 失败恢复：recovery 声明、when 匹配、runner 构造恢复任务。
- [workflow/issue-coordination.md](workflow/issue-coordination.md) — Issue、WorkflowRun、Runner、Session 的跨聚合交互。

## 支撑主题

- [repositories.md](repositories.md) — Repository 执行：Project 资源权威、Issue 绑定、dispatch 实时解析与最小 workspace identity（**WIP**）。
- [hermes-webhook.md](hermes-webhook.md) — Hermes 通知网关：事件类型、payload、签名与投递可靠性。
- [issue-breakdown.md](issue-breakdown.md) — 复合 Issue / 子 Issue 设计（**已定稿，待实装**）：父子模型、状态汇总、复合推进、与 Epic 的隔离约束；多仓库资源见 `docs/repositories.md`。
- [issue-templates.md](issue-templates.md) — 三类 issue 模板（Feature / Bug / Refactor）的 body 结构与设计依据。
- [prompt-management.md](prompt-management.md) — Project-scoped Prompt（**WIP**）、builtin fallback 和 Workflow key reference。
- [runner.md](runner.md) — Runner 与调度：聚合信息结构、poll presence、level-triggered 对账（claim / dispatch / supervision）。
- [task-log.md](task-log.md) — task 执行日志的采集管道、上报通道与存储归属。
- [web-ui.md](web-ui.md) — Web UI 设计边界。

## 决策记录

- [decisions/issue-owns-epic-membership.md](decisions/issue-owns-epic-membership.md) — Issue 持有当前 Epic 归属；Project-scoped number 身份与跨聚合恢复流程（issue-412）。
- [decisions/epic-status-revival.md](decisions/epic-status-revival.md) — Epic `done` 自动唤醒与 `closed` 拒绝 link（issue-392）。
