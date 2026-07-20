# Action 契约

Action 是 Workflow task 通过 `uses` 选择的一次执行接口。每个 Action 定义自己的
`with` 输入、输出和失败语义,但不拥有 Workflow 的完成判断,也不代表一个有身份的
Mohist Agent。

每个 Action 的契约是声明式的,包含三部分:

- **输入**:名称、是否必填、默认值。任务的 `with` 按声明校验——未知字段、缺少
  必填字段、类型不符都会被拒绝(保存 Profile 时即报错,而不是运行到才失败),
  不存在声明之外的隐藏输入。
- **输出**:成功时产出的字段,供 `setVars`、`${{ tasks.<id>.outputs.* }}` 和
  recovery 匹配读取。
- **错误码**:该 Action 全部业务失败的稳定标识目录,供 recovery
  `when: error.code=...` 匹配;错误文案面向人,不用于匹配。

本目录保存需要独立说明的 Action 产品契约。Workflow 的阶段、task、`expect` 和恢复
配置见 [Workflow Profile](../workflow-profiles.md);Action、Inline Agent 和 Mohist Agent
的关系见 [Agent 与 AgentSession](../agents.md)。

正文统一使用中文;产品中的规范术语、配置字段和命令保留原名。

## 当前 Action

- [`mohist/opencode`](opencode.md) —— 通过 OpenCode 执行一个回合,定义模型选项、
  Workflow Session 和 Session 操作语义。

未来加入 Pi 时,它会作为同层的独立 Action,而不是扩展 `mohist/opencode` 的输入。

## 实装差距

- 除 `mohist/opencode` 外,其余内置 Action(`mohist/push`、`mohist/rebase`、
  GitHub PR 系列、openspec 系列、`core/*`)尚无独立契约页,输入输出以实现为准;
  声明式契约落地后按声明补齐。
- 保存 Profile 时的输入校验尚未提供,当前未知字段被静默忽略。
