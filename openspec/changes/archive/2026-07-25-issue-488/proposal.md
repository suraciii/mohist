## Why

Agent 监管（supervisor）把生产线的审批与终态失败处理委托给一个 Mohist Agent，但今天要达成它只能手工拼装：`mo agent create` + 两条 `mo routing rule create`，连匹配表达式、身份指令和响应提示词都要自己写——这些提示词才是监管模式的真正产品内容，写错就退化成规则引擎。一条命令把这套权威内容装好，监管才从「能搭」变成「开箱即用」。

底座（路由求值与启动、Agent 启动、审批/失败事件、通知、`mo` 命令面对 Agent 可用）均已实装，前置 issue #489（`mo issue watch` 关注与静音）已完成；本 issue 是补上项目级监管的最后一块。

## What Changes

- 新增 CLI 子命令 `mo agent install <preset>`，接收内置预设名，当前仅支持 `supervisor`；未知名直接拒绝并列出可用预设。
- 安装按固定顺序、按名称幂等地创建三件产物（已存在则跳过并报告，不覆盖用户已编辑的内容）：
  - Agent `supervisor`（仅身份指令，无 AgentConfig / Skills / 并发覆盖）；
  - RoutingRule `supervisor-approval`，匹配 `event.type == "com.mohist.workflow.stage.approval-requested"`；
  - RoutingRule `supervisor-failure`，匹配 `event.type == "com.mohist.workflow.run.failed"`。
- 两条规则追加到**路由表末尾**（兜底位置，用户已有的针对性规则天然排在上方优先命中）；不设 `Continue`（独占响应）。
- 安装后做**只检查不修复**的前置提示：默认仓库工作区里 Agent 能否发现 `mohist` skill stub（`.agents/skills/mohist`）、owner 是否保留默认通知。检查失败不影响安装，但在输出中明确提示。
- 三份预设文本（身份指令、审批响应提示词、失败响应提示词，含 `{{event.*}}` 占位符）作为随 CLI 发布的资源原样写入，`install` 不渲染占位符。

不在本 issue 范围：`mo issue watch` 关注与静音（#489，已完成）；「Agent 响应失败」通知；审批决议的操作者记录（`--author` 落库）。这些是独立 issue。

## Capabilities

- `agent-preset-install`: 通过 `mo agent install <preset>` 安装内置 Agent 预设的命令面、预设名解析、幂等创建语义（按名跳过、规则追加到表尾）、前置检查提示，以及 `supervisor` 预设的权威内容定义（Agent 身份指令与审批/失败两条规则及其匹配表达式和响应提示词）。

## Impact

- **CLI**（`packages/cli/Mohist.Cli/`）：在 `MohistCliCommands.Agent.cs` 的 `agent` 命令下新增 `install` 子命令；新增预设目录/加载（参照 `skill-data` 的资源发布方式，如 `SkillAssetRootResolver`）；新增预设编排逻辑（解析名 → 幂等创建 Agent → 追加两条规则 → 前置检查与输出）。
- **预设资源**：随 CLI 发布的三份文本资源（supervisor 身份指令、`supervisor-approval` 提示词、`supervisor-failure` 提示词）。
- **复用既有 API**：`POST /api/projects/{projectId}/agents` 与 `POST /api/projects/{projectId}/routing/rules`（默认表尾追加），依赖规则名唯一冲突（`routing_rule_name_conflict`）做幂等判定。**不改动 server 端**。
- **文档**：`docs/agent-supervision.md` 与 `design/agent-supervision.md` 的「未实装 / 实装差距」小节随落地更新。
- **测试**：CLI install 命令的幂等性、名冲突跳过、表尾追加、未知预设拒绝、前置检查输出（外部依赖走 fake，不触真实 server）。
