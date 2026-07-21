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
- [`mohist/pi`](pi.md) —— 通过 Pi 执行一个回合;与 `mohist/opencode` 同层,共享
  模型选项形状与 Session 语义,但安装与信任边界不同。
- [Git Actions](git.md) —— workspace 准备、rebase、merge readiness 和 push 的显式输入契约。
- [GitHub PR Actions](github-pr.md) —— PR 创建、ready、状态检查和 squash merge 的显式输入契约。

Pi 是同层的独立 Action,不是 `mohist/opencode` 的输入扩展。

## 实装差距

- `mohist/pi` 尚未实装,当前只有产品契约(见 [pi.md](pi.md) 的实装差距小节)。
- Git Actions 和 GitHub PR Actions 的输入契约见对应产品契约页。OpenSpec 和 `core/*`
  的独立产品契约页仍待补齐。
- Runner 派发时会按 manifest 校验未知字段、必填字段和类型;自定义 Profile 应在 `with`
  中显式绑定需要的 Variable 值。
