# Runtime 集成

Runtime 集成把 Mohist 已经组装好的回合与 Session 请求适配到外部执行后端。它是
Workflow Action adapter 与 AgentJob executor 共享的基础能力；与 Agent / Session 的
所有权边界与不变量见 [`../agent-execution.md`](../agent-execution.md)。

本目录负责各 Runtime 特有的进程生命周期、SDK / protocol 映射、物理 Session 行为、
事件、状态核对和兼容性决策。

正文统一使用中文；领域标识、字段名、API 和代码符号保留原名。

- [OpenCode](opencode.md) —— `OpenCodeRuntime`、SDK 选择、物理 Session 生命周期、
  回合执行与 Session 命令。
- [Pi](pi.md) —— `PiRuntime`、进程内 SDK 接入、物理 Session 生命周期、回合执行与
  Session 命令；与 OpenCode 平行的独立深模块。

相关边界：

- [`../workflow/actions.md`](../workflow/actions.md) 定义通用 Workflow Action dispatch
  和输入输出契约。
- [`../../docs/actions/`](../../docs/actions/README.md) 定义面向使用者的各 Action 产品契约。

新增 Runtime 时为它增加独立文件；不为假想的共同点提前建立通用 Runtime 接口。
