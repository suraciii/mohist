## ADDED Requirements

### Requirement: workflow.yaml 定义工作流阶段和 prompt 模板
系统 SHALL 支持项目根目录或 `.mohist/` 目录下的 `workflow.yaml` 文件，声明式定义工作流阶段及每个阶段的 prompt 模板。

#### Scenario: 读取默认 workflow
- **WHEN** 项目没有 `workflow.yaml` 文件
- **THEN** `read_workflow` tool 返回内置默认 workflow（plan → build → check → done，每阶段包含默认 prompt）

#### Scenario: 读取自定义 workflow
- **WHEN** 项目根目录存在 `workflow.yaml`，内容包含 stages 和 prompt 字段
- **THEN** `read_workflow` tool 返回该文件内容，Main Agent 按此编排

#### Scenario: workflow 文件格式错误
- **WHEN** `workflow.yaml` 存在但 YAML 解析失败
- **THEN** 返回错误信息，建议使用默认 workflow

### Requirement: workflow.yaml 项目级配置
系统 SHALL 只实现项目级 workflow.yaml（项目根目录或 `.mohist/workflow.yaml`），不实现全局配置（`~/.mohist/workflow.yaml` 留作 M2+）。

#### Scenario: 优先级读取
- **WHEN** 同时存在 `./workflow.yaml` 和 `.mohist/workflow.yaml`
- **THEN** 优先读取 `./workflow.yaml`

### Requirement: read_workflow tool 提供配置给 Main Agent
系统 SHALL 提供 `read_workflow` tool，返回 workflow.yaml 内容（不包含变量替换）给 Main Agent 的 LLM 理解和执行。

#### Scenario: Main Agent 读取 workflow
- **WHEN** Main Agent 调用 `read_workflow()`
- **THEN** 返回 workflow.yaml 的原始内容（或默认 workflow），包含每个 stage 的 name、prompt、approval、timeout 配置

### Requirement: workflow prompt 变量替换由 spawn_coder 处理
系统 SHALL 由 `spawn_coder` tool 接收 prompt 模板和变量，内部完成替换后再发送给 opencode acp。

#### Scenario: spawn_coder 接收 template 和 variables
- **WHEN** Main Agent 调用 `spawn_coder({ taskTemplate: "分析 issue #{issue.number}", variables: { issue: { number: 3, title: "用户登录" } } })`
- **THEN** spawn_coder 内部将 `{issue.number}` 替换为 `3`，将结果发送给 opencode

#### Scenario: 引用前序阶段输出
- **WHEN** build 阶段 spawn_coder 调用包含 `variables: { plan: { output: "计划内容..." } }`
- **THEN** prompt 模板中的 `{plan.output}` 被替换为 "计划内容..."

#### Scenario: 变量缺失
- **WHEN** prompt 引用了 `{build.output}` 但 build 阶段尚未执行（变量未提供）
- **THEN** 替换为 `"(尚未执行)"`
