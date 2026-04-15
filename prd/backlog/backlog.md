# Product Backlog

> 从设计讨论中搁置延后的事项。按类别分组，标注所属 Milestone。

---

## 1. 文档清理

| ID | 事项 | 说明 | 状态 |
|----|------|------|------|
| B-001 | 归档 design/ 旧文档 | tech-spec.md、issueflow.md、workflow.md 描述的是旧架构（GitHub Labels、crawlph），应移入 archive 或标注为历史参考 | ✅ 已完成 (doc-cleanup-and-stage-model T-002) |
| B-002 | 更新 prd/ 文档 | prd/ 下文档仍使用旧名 crawlph，且 PRD 定义 5 阶段 (Explore/Plan/Dev/Verify) 与实现不一致 | ✅ 已完成 (doc-cleanup-and-stage-model T-010~T-013) |
| B-003 | 更新 openspec/specs/ 全局 spec | 大部分 spec 描述的是确定性状态机架构（如 workflow-engine、issue-workflow），agent 架构改造后需要更新或标注 REMOVED | ✅ 已完成 (doc-cleanup-and-stage-model T-006) |

---

## 2. 代码质量（现有代码库）

| ID | 事项 | 说明 | Milestone |
|----|------|------|-----------|
| B-004 | apiClient 重复实现 | cli/commands/issue.ts 和 cli/commands/quick.ts 各自实现了一份完全相同的 apiClient() 函数（30+ 行），应抽到公共模块 | ✅ 已完成 (code-cleanup-api-layer T-001) |
| B-005 | CLI 业务逻辑泄漏 | issue.ts approve 命令直接执行 git merge（违反 thin client 原则），合并逻辑应在 server 端 | ✅ 已完成 (code-cleanup-api-layer T-004) |
| B-006 | ~~config 中旧命名残留~~ | ~~quick.ts:128-129 的 usage 提示仍为 "crawlph config"~~ | ✅ 已清理 |
| B-007 | StateManager 和 WorkflowService 职责重叠 | 两个类都能修改 issue stage/status，StateManager 不检查规则，WorkflowService 检查规则但存在绕过路径 | ✅ 已完成 (code-cleanup-api-layer T-008) |

---

## 3. Milestone 2: 能交互

### Event Bus

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-010 | 实现内存 Event Bus | callback-based Set<listener>，publish/subscribe/unsubscribe，所有事件携带 issueId | design D5 |
| B-011 | Event Bus 内存泄漏防护 | sub-agent 结束时清理所有 listener，使用 WeakRef 或显式 unsubscribe | design Risk |

### ask_user + gate 暂停

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-020 | 实现 ask_user 工具 | Deferred + Pending Map，阻塞 agent loop 直到用户回答。参考 opencode Question 模块 | design D6 |
| B-021 | 实现 gate_after 机制 | gate_after=true 的阶段完成后暂停 agent loop，等待用户 approve | design D6 |
| B-022 | ask_user 无 Channel 时的降级 | 没有 channel 在线时 ask_user 会永久阻塞，需要降级策略（跳过或默认行为） | design Risk |

### mo attach + 消息注入

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-030 | 实现 mo attach CLI 命令 | 连接 server，订阅 issue 事件（SSE），渲染 agent 输出到终端，stdin 发送用户消息 | design D7 |
| B-031 | 实现用户消息注入 | 外部消息注入到 Main Agent session，触发新 LLM loop 迭代 | design D7 |
| B-032 | ~~mo attach 连接协议确定~~ | ✅ 已完成 — SSE 已为实现事实，通过 `mo attach` SSE 订阅实现 (m2-close-interaction) |
| B-033 | ~~ask_user 与自由文本的冲突处理~~ | ✅ 已完成 — 通过 `mo attach` 统一交互入口解决，QUESTION_MODE 自动路由用户输入到 `/questions/:id/reply` (m2-close-interaction) |

### HTTP API 扩展

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-040 | 新增 question API | GET /api/questions、POST /api/questions/:id/reply、POST /api/questions/:id/reject | prd.json T-020 |
| B-041 | 新增 events SSE 端点 | GET /api/issues/:id/events（SSE 流） | prd.json T-020 |
| B-042 | 新增消息注入端点 | POST /api/issues/:id/messages | prd.json T-020 |

---

## 4. Milestone 3: 能配置

### workflow.yaml

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-050 | 实现 workflow.yaml loader | 读取 .mohist/workflow.yaml，解析 stages（agent、description、expects、gate_after），Zod 校验 | design D4 |
| B-051 | 内置默认 workflow | 项目无 workflow.yaml 时回退到内置默认流程 | prd.json T-012 |
| B-052 | workflow.yaml 条件分支 | 未来可能需要，当前 Non-Goal | design Non-Goal |

### 完整 Sub-Agent 体系

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-060 | 重构 ExploreAgent | system prompt 重写为结构化面试模式，新增 write_file 工具，产出 proposal.md 到 openspec/changes/。面试流程：收集想法→探索代码库→逐分支追问→确认模块→写 proposal | talks/2026-04-15 决策1,5,9,10 |
| B-061 | 重构 PlannerAgent | 从一步全出 JSON 改为：读 proposal.md → 探索代码库 → 产出 specs/ + design.md + tasks.json。自审查改为方案完整性+拆分质量（人类审查预演） | talks/2026-04-15 决策3,4,8,11 |
| B-062 | 重构 ReviewerAgent | 从 4 维度改为 6-pass 代码审查 + 跨切面审计。核心定位：对抗性审查，审查者是"对手" | talks/2026-04-15 决策7,14 |
| B-063 | 产物目录迁移 | 产物路径从 .mohist/changes/ 改为代码库内 openspec/changes/{issueNumber}-{slug}/，纳入版本控制。ChangeArtifactsManager 适配新路径 | talks/2026-04-15 决策3 |
| B-064 | tasks.json 替代 prd.json | 新增 type(WRITE/TEST/MIGRATE/CONFIG/REVIEW)、mode(AFK/HITL)、output、dependsOn、files、patterns 字段。context-assembler 适配新结构。types: PrdTask → Task | talks/2026-04-15 决策4,13 |
| B-065 | Task 获取 tool | mohist agent 通过 tool 从 tasks.json 获取当前应执行的任务（按 order、依赖关系、状态过滤），传给 coder session | talks/2026-04-15 决策13 |
| B-066 | Sub-Agent 类型定义 | agent 的 name、system prompt、tool set、model override 定义 | prd.json T-013 |

### 基础工具（给非 Code sub-agent 用）

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-070 | read_file 工具 | 读取 worktree 中文件内容 | prd.json T-006 |
| B-071 | glob 工具 | 按模式查找文件 | prd.json T-006 |
| B-072 | grep 工具 | 搜索文件内容 | prd.json T-006 |
| B-073 | write_file 工具 | 写入文件（Plan Agent 用） | prd.json T-007 |
| B-074 | bash 工具 | 执行 shell 命令（Check Agent 用），可配置 timeout | prd.json T-007 |

### Main Agent prompt 配置化

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-080 | System prompt builder | 动态生成 prompt：role + tools + issue context + workflow stages + gate semantics | prd.json T-014 |

---

## 5. Milestone 4: 收尾

### Session 恢复

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-090 | Session 恢复流程 | Server 重启后从 SQLite 恢复 Main Agent session，重新评估当前状态继续执行 | design D8 |

### 多 Issue 并发

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-091 | per-issue 独立 session | 每个活跃 issue 一个 agent session + LLM loop | design D12 |
| B-092 | 并发资源管理 | LLM API 并发限制、内存使用控制 | design D12 |

### 进度追踪

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-100 | 实现 workflow_log 表 | append-only，记录 stage_enter/exit、agent_spawn/done、decision、human_action | design D9 |
| B-101 | mo status 重做 | 基于 workflow_log 的时间线展示，替代当前基于 stage 字段的简单展示 | design D9 |

### Rollback

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-110 | 实现用户回退机制 | 用户发回退命令 → Main Agent cancel sub-agent → 更新 stage → 重新 spawn | design D10 |

### 清理

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-120 | 删除旧 workflow/ 代码 | engine.ts、issue-workflow.ts、stage-handlers.ts | prd.json T-021 |
| B-121 | 删除旧 agent/ 代码 | runner.ts、prompts.ts | prd.json T-021 |
| B-122 | ~~更新 types/index.ts~~ | ~~Stage 枚举从硬编码改为动态字符串（纳入 Stage 架构 PBI）~~ | ✅ 已更新为 plan/build/check (doc-cleanup-and-stage-model T-007) |

---

## 6. Stage 架构 + 工作流对齐 (已决策)

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
| M3 | 可配置 pipeline，用户可加减阶段 | workflow.yaml |
| 远期 | Pipeline as Code（条件分支、并行、插件） | workflow.yaml 扩展 |

**来源**: 2026-04-01 explore 讨论 → `talks/2026-04-01-stage-model.md`
**更新**: 2026-04-15 工作流对齐 → `talks/2026-04-15-workflow-alignment.md`

---

## 7. Web UI

### HTTP 框架迁移

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-300 | Express → Hono 迁移 | 迁移 HTTP 框架到 Hono，获得内置 SSE (streamSSE)、更好的类型安全、更好的静态文件服务。业务层/services/db 不受影响 | 2026-04-03 explore |

### 嵌入式 Web UI

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-301 | 嵌入式 Web UI 架构 | 参考 opencode 做法：Vite build → 嵌入 server 二进制 → `mo server` 一条命令同时提供 API + UI | 2026-04-03 explore |
| B-302 | 前端技术选型 | SolidJS (opencode 同款，无虚拟DOM) vs React (生态大)。UI 相对简单（看板+列表+详情），两者都够用 | 2026-04-03 explore |
| B-303 | SSE 实时事件推送 | 三层架构：内部 Bus (PubSub) → SSE endpoint → 客户端。支持 Agent 进度实时展示 | 2026-04-03 explore |
| B-304 | 实时进度分阶段实现 | MVP (Level 1): 状态级事件（stage_changed/comment_added/agent_done/error），数据已在 SQLite 中，Bus 包裹现有 repo 操作即可。Level 2 (后续): 动作级事件（tool_call/tool_result），需要接通 Vercel AI SDK stream 和 ACP sessionUpdate | 2026-04-03 explore |

### Web UI 功能

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-310 | 看板视图 | Issue 按 stage (draft/plan/build/check/done) 分列展示 | 2026-04-03 explore |
| B-311 | Issue 详情 | 描述、评论、Agent 输出流、状态变更历史 | 2026-04-03 explore |
| B-312 | 项目切换 | 多项目之间切换，当前项目高亮 | 2026-04-03 explore |
| B-313 | 操作能力（MVP 后） | 创建 Issue、启动 Agent、审批 gate、关闭/重开 Issue | 2026-04-03 explore |

### ACP 执行可观测性

| ID | 事项 | 说明 | 来源 |
|----|------|------|------|
| B-320 | ACP sessionUpdate 事件捕获 | 当前 spawn-coder.ts 只收集 agent_message_chunk，丢弃了 plan/tool_call/tool_call_update 等事件。应在 oneshot 执行过程中捕获所有 sessionUpdate 事件，用于执行后诊断和实时进度展示。不需要 persistent session，oneshot 过程中事件已经在发送 | 2026-04-05 explore |
| B-321 | 执行日志持久化 | 将捕获的 ACP 事件存入 workflow_log 表（B-100），支持事后按时间线回放：哪些文件被读取/修改、哪些命令被执行、成功/失败状态。mohist agent 可基于日志做失败分析，用户可在 Web UI 查看 | 2026-04-05 explore |
| B-322 | 输出截断问题 | 当前 spawn-coder.ts 对 agent 输出硬截断 8000 字符（head 3000 + tail 5000），build 阶段容易丢失关键信息。应改为完整输出存文件或数据库，返回摘要而非截断 | 2026-04-05 explore |

---

## 8. 跨 Milestone

| ID | 事项 | 说明 | Milestone |
|----|------|------|-----------|
| B-200 | 多模型配置 | Main Agent 用便宜/快模型（haiku），Code Agent 用强模型（sonnet）。需要 provider 配置支持 model override | M3+ |
| B-201 | LLM provider 配置格式 | ~/.mohist/config.json 结构定义（provider、model、api key 等） | M1（最小版）→ M3（完整版） |
| B-203 | recoverState() 重做 | 当前 server 重启后直接标记所有 running tasks 为 failed，agent 架构后应恢复 session | M4 |
| B-204 | Provider 接口瘦身 | IssueProvider 定义了 github/lab 类型但只有 local 实现，接口设计过度 | M4 |
| B-205 | Compaction 策略 | 长 issue 的 session messages 可能超过 context window，需要自动摘要机制 | 未来 |

---

## 9. 用户反馈想法

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

