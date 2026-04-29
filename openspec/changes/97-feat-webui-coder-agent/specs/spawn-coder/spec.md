## MODIFIED Requirements

### Requirement: spawn_coder 启动 opencode acp oneshot session

系统 SHALL 提供 `spawn_coder` tool，通过 `opencode acp --cwd <worktree>` 启动 oneshot coding agent 子进程，接收 taskTemplate 和 variables，内部完成变量替换后发送给 opencode。

模型优先级 SHALL 为（从高到低）：
1. `model` 参数（per-issue override）
2. `config.opencode.stageModels[stage]`（per-stage override，仅当 stage 匹配时）
3. `config.opencode.model`（全局默认）

当 per-issue model 已通过 `setSessionConfigOption` 设置时，系统 SHALL 跳过 `config.opencode.model` 和 `config.opencode.stageModels` 的 `setSessionConfigOption` 调用。

#### Scenario: 成功执行 plan 阶段任务

- **WHEN** Main Agent 调用 `spawn_coder({ taskTemplate: "分析 issue: {issue.title}", variables: { issue: { title: "用户登录" } } })`
- **THEN** 系统将 template 替换为 "分析 issue: 用户登录"，启动 `opencode acp --cwd <worktree>` 子进程，通过 stdio JSON-RPC 发送 `initialize` → `session/new` → `session/prompt(替换后的task)`，等待响应返回结果文本

#### Scenario: coding agent 超时

- **WHEN** coding agent 在配置的超时时间（默认 30 分钟）内未完成
- **THEN** 系统向 opencode acp 发送 `session/cancel`，kill 子进程，返回超时错误信息

#### Scenario: coding agent 进程启动失败

- **WHEN** `opencode acp` 命令不在 PATH 中或启动报错
- **THEN** 返回包含错误信息的失败结果，不崩溃

#### Scenario: per-issue model 覆盖全局默认 model

- **WHEN** `model` 参数为 `"anthropic/claude-sonnet-4"`（per-issue override）
- **AND** `config.opencode.model` 为 `"openai/gpt-4"`（全局默认）
- **THEN** 系统 SHALL 仅调用一次 `setSessionConfigOption` 设置 `"anthropic/claude-sonnet-4"`
- **AND** 不 SHALL 调用 `setSessionConfigOption` 设置 `"openai/gpt-4"`

#### Scenario: per-issue model 覆盖 per-stage model

- **WHEN** `model` 参数为 `"anthropic/claude-sonnet-4"`（per-issue override）
- **AND** `config.opencode.stageModels.build` 为 `"openai/gpt-4"`（per-stage override）
- **THEN** 系统 SHALL 仅调用 `setSessionConfigOption` 设置 `"anthropic/claude-sonnet-4"`
- **AND** 不 SHALL 调用 `setSessionConfigOption` 设置 `"openai/gpt-4"`

#### Scenario: 无 per-issue model 时使用 per-stage model

- **WHEN** `model` 参数为 undefined 或 null
- **AND** `config.opencode.stageModels.build` 为 `"openai/gpt-4"`
- **THEN** 系统 SHALL 调用 `setSessionConfigOption` 设置 `"openai/gpt-4"`

#### Scenario: 无 per-issue 和 per-stage model 时使用全局默认

- **WHEN** `model` 参数为 undefined 或 null
- **AND** 无匹配的 `config.opencode.stageModels[stage]`
- **AND** `config.opencode.model` 为 `"anthropic/claude-sonnet-4"`
- **THEN** 系统 SHALL 调用 `setSessionConfigOption` 设置 `"anthropic/claude-sonnet-4"`

#### Scenario: 所有 model 均未配置

- **WHEN** `model` 参数为 undefined 或 null
- **AND** 无匹配的 per-stage model
- **AND** `config.opencode.model` 未配置
- **THEN** 系统 SHALL 不调用 `setSessionConfigOption`，使用 opencode 内部默认模型
