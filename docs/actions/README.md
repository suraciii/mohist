# Action 契约

Action 是 Workflow task 通过 `uses` 选择的一次执行接口。每个 Action 定义自己的
`with` 输入、输出和失败语义，但不拥有 Workflow 的完成判断，也不代表一个有身份的
Mohist Agent。

本目录保存需要独立说明的 Action 产品契约。Workflow 的阶段、task、`expect` 和恢复
配置见 [Workflow Profile](../workflow-profiles.md)；Action、Inline Agent 和 Mohist Agent
的关系见 [Agent 与 AgentSession](../agents.md)。

正文统一使用中文；产品中的规范术语、配置字段和命令保留原名。

## 当前 Action

- [`mohist/opencode`](opencode.md) —— 通过 OpenCode 执行一个回合，定义模型选项、
  Workflow Session 和 Session 操作语义。

未来加入 Pi 时，它会作为同层的独立 Action，而不是扩展 `mohist/opencode` 的输入。
