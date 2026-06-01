# Product Backlog

> 从设计讨论中搁置延后的事项。按类别分组，标注所属 Milestone。
> 最后整理: 2026-06-01（清理已完成项、标注 ASP.NET Core 架构下已过时项）

---

## 1. Milestone 2: 能交互

> 大部分已实现。以下为尚未交付的细项。

### ask_user 降级

| ID | 事项 | 说明 | ASP.NET Core 状态 |
|----|------|------|------------------|
| B-022 | ask_user 无 Channel 时的降级 | 没有 channel 在线时 ask_user 会永久阻塞，需要降级策略（跳过或默认行为） | 评估中 — 需要检查当前 .NET 实现的降级行为 |

---

## 2. Milestone 3: 能配置

### workflow.yaml 条件分支

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-052 | workflow.yaml 条件分支 | 未来可能需要，当前 Non-Goal | design Non-Goal |

### Sub-Agent 体系（待评估 .NET 架构下的实现形态）

> TypeScript agent 体系已迁移到 .NET Orleans Grain + opencode ACP。以下项需评估是否以 Orleans profile 扩展实现。

| ID | 事项 | 说明 |
|----|------|------|
| B-060 | 重构 ExploreAgent | 外部 mohist-explore skill，非内置 sub-agent |
| B-062 | 重构 ReviewerAgent | 对抗性审查已在 workflow Check 阶段实现，可评估是否需要增强 |
| B-066 | Sub-Agent 类型定义 | agent 的 name、system prompt、tool set、model override 定义 — 已通过 workflow profile YAML 实现 |

---

## 3. Milestone 4: 收尾

### 多 Issue 并发

| ID | 事项 | 说明 | API Issue |
|----|------|------|-----------|
| B-091 | per-issue 独立 session | 已在 Orleans Grain 架构中实现 | — |
| B-092 | 并发资源管理 | LLM API 并发限制、内存使用控制 | #22 (runner 并发), #44 (integrate 锁) |

### 进度追踪

| ID | 事项 | 说明 | API Issue |
|----|------|------|-----------|
| B-100 | 实现 workflow_log 表 | 已实现为 WorkflowEvents 表 | — |
| B-101 | mo status 重做 | 基于 WorkflowEvents 的时间线展示已实现（workflow timeline API） | — |

### Rollback

| ID | 事项 | 说明 | API Issue |
|----|------|------|-----------|
| B-110 | 实现用户回退机制 | 通过 rerun/retry API 实现 | #29 (rerun actions) |

### 旧代码清理

| ID | 事项 | 说明 | ASP.NET Core 状态 |
|----|------|------|------------------|
| B-120 | 删除旧 workflow/ 代码 | engine.ts、issue-workflow.ts、stage-handlers.ts | .NET 迁移已不涉及 TS 旧代码 |
| B-121 | 删除旧 agent/ 代码 | runner.ts、prompts.ts | .NET 迁移已不涉及 TS 旧代码 |

---

## 4. Stage 架构 + 工作流对齐 (已决策)

> **决策结果**: Explore(独立能力) + PLAN → BUILD → CHECK 三阶段循环模型
>
> 产物体系对齐 OpenSpec: proposal → specs + design → tasks
>
> 详见 `design/explore.md`、`design/plan.md`、`design/build.md`、`design/check.md`
> 和 `talks/2026-04-15-workflow-alignment.md`

**已决策**:

| 决策 | 结论 | 理由 |
|------|------|------|
| 产物体系 | proposal → specs + design → tasks (OpenSpec 模型) | 对齐 Mario Barbero 工作流 + OpenSpec 概念体系 |
| 产物位置 | openspec/changes/{N}-{slug}/ (代码库内) | 纳入版本控制，可 review |
| Issue 角色 | 追踪载体，不承载内容 | 产物在 openspec/changes/，Issue 只管状态流转 |
| Explore 定位 | 独立能力，不参与 stage 状态机 | 可从 Explore 创建 Issue，也可在已有 Issue 下 Explore |
| Explore 交互 | 同步阻塞式结构化面试 | 用 ask_user，行为从偶尔问小问题变为系统化面试 |
| Plan 产出 | specs/ + design.md + tasks.json | 不再一步全出 JSON，基于 proposal 分步产出 |
| tasks.json | prd.json 改名 + 增加字段 (type/mode/output/dependsOn/files/patterns) | 命名与职责对齐，增加 AFK/HITL 分类 |
| Plan gate | 单 gate，展示自审查报告等用户批准或反馈 | 不拆子 gate |
| 自审查 | 人类审查的预演，AI 先按同样标准审一遍 | 降低人类审查成本 |
| Check 定位 | 对抗性审查，6-pass + 跨切面审计 | 审查者是"对手"，不是"同事" |
| Task 执行 | 每个 task 独立 ACP session，通过 tool 获取 next task | "一次会话一个 task"是机制不是约束 |

**Stage 枚举**: `draft | plan | build | check | done`

**演进路径**:

| 阶段 | Stage 定义 | 配置方式 |
|------|-----------|---------|
| M1 | 硬编码 3 阶段 (plan → build → check)，gate 属性 | system prompt |
| M2 | 默认 3 阶段 + gate_after 机制 | config/table |
| M3 | 可配置 workflow，用户可加减阶段 | workflow.yaml |
| 远期 | Workflow as Code（条件分支、并行、插件） | workflow.yaml 扩展 |

**来源**: 2026-04-01 explore 讨论 → `talks/2026-04-01-stage-model.md`
**更新**: 2026-04-15 工作流对齐 → `talks/2026-04-15-workflow-alignment.md`

---

## 5. 跨 Milestone

| ID | 事项 | 说明 | 状态 |
|----|------|------|------|
| B-200 | 多模型配置 | Main Agent 用便宜/快模型（haiku），Code Agent 用强模型（sonnet）。需要 provider 配置支持 model override | 部分完成 — 已通过 per-stage model override 实现 |
| B-201 | LLM provider 配置格式 | ~/.mohist/config.jsonc 结构定义（provider、model、api key 等） | 已实现 |
| B-203 | recoverState() 重做 | 当前 server 重启后直接标记所有 running tasks 为 failed，agent 架构后应恢复 session | .NET Orleans 天然支持 — Grain 激活自动恢复 |
| B-204 | Provider 接口瘦身 | IssueProvider 定义了 github/lab 类型但只有 local 实现，接口设计过度 | ❌ 已过时 — .NET 迁移后不适用 |
| B-205 | Compaction 策略 | 长 issue 的 session messages 可能超过 context window，需要自动摘要机制 | 未来 |

---

## 6. 用户反馈想法

来自实际使用中的想法，待评估和规划。

### 智能代理行为

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-400 | mohist agent 应像人类一样响应 opencode | 当 opencode agent 询问问题时，mohist 应先尝试自己理解并解决，不能解决再询问用户 | 用户反馈 |

### 多渠道交互

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-401 | 支持 IM 交互（Telegram 等） | 用户可通过 Telegram 等即时通讯工具与 mohist 交互，而不仅限于 CLI | 用户反馈 |

### 第三方集成

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-403 | GitHub 集成同步 | 将 mohist 的 issue/issue comments/pr comments 同步到 GitHub，实现双向数据同步 | 用户反馈 |

### 模型配置

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-402 | 允许切换 explore 使用的 model | 用户可配置 explore 模式使用的 LLM 模型（如从默认模型切换到更强的模型） | 用户反馈 |
